using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Streamsemble.AirPlay.Common.Video;
using Streamsemble.Core.Video;

namespace Streamsemble.AirPlay.Receiver.Video;

/// <summary>
/// The receiver half of an AirPlay screen-mirroring stream (SETUP type 110).
/// We listen on a TCP port, hand it back as <c>dataPort</c>, and the sender
/// connects in and pushes 128-byte-headed packets: encrypted H.264 access units,
/// plaintext codec configs, and heartbeats.
///
/// Structured like <see cref="Audio.RealtimeAudioServer"/>, with one difference
/// that shapes everything: video rides TCP, so there is no reorder window and no
/// loss to paper over. A framing error is not a dropped packet, it is a stream
/// that has lost its place — so a header that does not parse ends the
/// connection rather than being skipped.
/// </summary>
public sealed class MirrorDataServer : IDisposable
{
    private readonly ILogger _logger;
    private readonly H264AccessUnitAssembler _assembler;

    /// <summary>
    /// The ciphers still in the running. A mirroring SETUP names no key, so
    /// which derivation the sender used is settled by trying them against real
    /// traffic: AVCC framing is self-validating — the length prefixes have to
    /// tile the payload exactly — so a candidate that produces a parseable
    /// access unit is the right one and the rest are discarded.
    /// </summary>
    private readonly List<(string Source, IMirrorStreamCipher Cipher)> _candidates;
    private IMirrorStreamCipher? _cipher;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Whether the "is it even encrypted?" question has been asked yet.</summary>
    private bool _plaintextChecked;

    /// <summary>
    /// The modern ChaCha20-Poly1305 envelope, once its HKDF direction is known,
    /// and the two directions to try until one authenticates.
    /// </summary>
    private MirrorDataStreamCipher? _dataStream;
    private IReadOnlyList<MirrorDataStreamCipher>? _dataStreamCandidates;

    /// <summary>Offers the modern envelope, tried ahead of the legacy AES-CTR one.</summary>
    public IReadOnlyList<MirrorDataStreamCipher>? DataStreamCandidates
    {
        init => _dataStreamCandidates = value;
    }

    public MirrorDataServer(IReadOnlyList<(string Source, IMirrorStreamCipher Cipher)> candidates, ILogger logger)
    {
        _candidates = [.. candidates];
        _logger = logger;
        _assembler = new H264AccessUnitAssembler(logger);
        if (_candidates.Count == 1)
        {
            _cipher = _candidates[0].Cipher;
        }
    }

    public MirrorDataServer(IMirrorStreamCipher cipher, ILogger logger)
        : this([("SETUP", cipher)], logger)
    {
    }

    public int Port { get; private set; }

    /// <summary>Called per access unit, in decode order, from the socket read loop.</summary>
    public required Func<VideoFrame, CancellationToken, ValueTask> OnAccessUnit { get; init; }

    /// <summary>Called when the sender states (or restates) its parameter sets.</summary>
    public Action<VideoCodecConfig>? OnCodecConfig { get; init; }

    /// <summary>
    /// Translates a packet's sender-clock presentation stamp into grandmaster
    /// nanoseconds. Returns 0 while the timing exchange has not locked yet, in
    /// which case the frame goes out unstamped and the sink falls back to its
    /// own pacing rather than scheduling against a meaningless number.
    /// </summary>
    public required Func<ulong, long> MapTimestamp { get; init; }

    /// <summary>Raised when the sender closes the data channel — mirroring has stopped.</summary>
    public Action? OnClosed { get; init; }

    public long AccessUnitCount => _assembler.AccessUnitCount;

    public long KeyframeCount => _assembler.KeyframeCount;

    public VideoCodecConfig? CodecConfig => _assembler.Config;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.IPv6Any, 0);
        _listener.Server.DualMode = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = AcceptAsync(_listener, _cts.Token);
        _logger.LogInformation("mirror data listener on TCP :{Port} ({Keying})", Port,
            _cipher?.Describe() ?? $"{_candidates.Count} key candidates, settled on the first parseable frame");
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("mirror data channel connected from {Remote}", client.Client.RemoteEndPoint);
                await ServeAsync(client, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "mirror data channel closed after {Units} access units ({Keyframes} keyframes)",
                    _assembler.AccessUnitCount, _assembler.KeyframeCount);
                OnClosed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mirror data listener failed");
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        var header = new byte[MirrorPacketHeader.Length];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
                {
                    return;
                }

                MirrorPacketHeader parsed;
                try
                {
                    parsed = MirrorPacketHeader.Parse(header);
                }
                catch (InvalidDataException ex)
                {
                    // Once the byte stream is off frame nothing after it is
                    // recoverable, so say what actually arrived and stop.
                    _logger.LogError(ex, "mirror data channel out of frame; first 16 bytes were {Head}{Hint}",
                        Convert.ToHexString(header.AsSpan(0, 16)), HttpVerbHint(header));
                    return;
                }

                var payload = new byte[parsed.PayloadLength];
                if (parsed.PayloadLength > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
                {
                    _logger.LogInformation("mirror data channel closed mid-payload ({Bytes} B expected)", parsed.PayloadLength);
                    return;
                }

                await HandlePacketAsync(parsed, header, payload, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async ValueTask HandlePacketAsync(
        MirrorPacketHeader header, byte[] rawHeader, byte[] payload, CancellationToken ct)
    {
        switch (header.PayloadType)
        {
            case MirrorPayloadType.CodecConfig:
                // Deliberately not decrypted: the config packet is the one
                // payload the protocol sends in the clear.
                if (_assembler.AcceptConfig(payload) is { } config)
                {
                    OnCodecConfig?.Invoke(config);
                }

                break;

            case MirrorPayloadType.Video:
                if (ResolveCipher(rawHeader, payload) is not { } plaintext)
                {
                    return;
                }

                var targetNanos = MapTimestamp(header.TimestampNtp);
                if (_assembler.AcceptAccessUnit(plaintext, targetNanos) is { } frame)
                {
                    if (_assembler.AccessUnitCount == 1)
                    {
                        _logger.LogInformation(
                            "mirror video flowing: first access unit {Bytes} B, keyframe {Key}, stamped {Lead:F0} ms out",
                            frame.Data.Length, frame.IsKeyframe,
                            targetNanos == 0 ? 0 : (targetNanos - Timing.Ptp.PtpReceiverClock.NowNanos) / 1e6);
                    }

                    await OnAccessUnit(frame, ct).ConfigureAwait(false);
                }

                break;

            case MirrorPayloadType.Heartbeat:
                _logger.LogTrace("mirror heartbeat");
                break;

            default:
                _logger.LogDebug("mirror packet type {Type} ignored ({Bytes} B)", (byte)header.PayloadType, payload.Length);
                break;
        }
    }

    /// <summary>
    /// Turns the commonest way this goes wrong into a legible message: a sender
    /// that opened an HTTP conversation on the data port instead of the framed
    /// stream means the SETUP reply pointed it somewhere unexpected.
    /// </summary>
    private static string HttpVerbHint(ReadOnlySpan<byte> header)
    {
        var head = Encoding.ASCII.GetString(header[..4]);
        return head is "POST" or "GET " or "SETU"
            ? $" — that is an HTTP/RTSP request (\"{head.Trim()}\"), not mirror framing"
            : "";
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    /// <summary>
    /// Decrypts a video payload, settling which candidate cipher is right if
    /// that is still open. A stream cipher cannot tell us itself — it will
    /// happily produce noise — so the test is whether the plaintext parses as
    /// AVCC, which a wrong key effectively never does.
    /// </summary>
    private byte[]? ResolveCipher(byte[] headerBytes, byte[] payload)
    {
        // Before assuming anything, look at what actually arrived. The config
        // packet on this same channel is sent in the clear, so "encrypted" is
        // an assumption about the video payload, not an observation — and a
        // stream cipher will never tell you it was the wrong choice, it just
        // returns noise. AVCC framing settles it: if the RAW bytes already
        // tile as NAL units, decrypting them is what would break the stream.
        if (!_plaintextChecked)
        {
            _plaintextChecked = true;
            _logger.LogInformation(
                "first mirror video payload ({Bytes} B) begins {Head}", payload.Length,
                Convert.ToHexString(payload.AsSpan(0, Math.Min(16, payload.Length))));

            if (H264Nal.LooksLikeAvcc(payload))
            {
                _logger.LogInformation(
                    "mirror video is NOT encrypted — the payload is already AVCC framed; using it verbatim");
                (_cipher as IDisposable)?.Dispose();
                _cipher = new NullMirrorCipher();
                _candidates.Clear();
                return payload;
            }
        }

        // The modern envelope authenticates the header, so it needs it.
        if (_dataStream is { } aead)
        {
            try
            {
                return aead.Open(headerBytes, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mirror video packet failed to decrypt ({Bytes} B)", payload.Length);
                return null;
            }
        }

        if (_dataStreamCandidates is { Count: > 0 } candidates)
        {
            foreach (var candidate in candidates)
            {
                candidate.Reset();
                try
                {
                    var opened = candidate.Open(headerBytes, payload);
                    _dataStream = candidate;
                    _dataStreamCandidates = null;
                    _logger.LogInformation("mirror video key resolved: {Cipher}", candidate.Describe());
                    return opened;
                }
                catch
                {
                    // The tag said no. Nothing consumed — Reset put the counter back.
                }
            }

            _logger.LogError(
                "no data-stream key authenticates the mirror video; falling back to the legacy AES-CTR envelope");
            _dataStreamCandidates = null;
        }

        if (_cipher is { } settled)
        {
            try
            {
                return settled.Decrypt(payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mirror video packet failed to decrypt ({Bytes} B)", payload.Length);
                return null;
            }
        }

        foreach (var (source, candidate) in _candidates)
        {
            byte[] plaintext;
            try
            {
                plaintext = candidate.Decrypt(payload);
            }
            catch
            {
                continue;
            }

            if (!H264Nal.LooksLikeAvcc(plaintext))
            {
                continue;
            }

            _cipher = candidate;
            _logger.LogInformation("mirror video key resolved: {Source} ({Cipher})", source, candidate.Describe());
            foreach (var (_, loser) in _candidates.Where(c => !ReferenceEquals(c.Cipher, candidate)))
            {
                (loser as IDisposable)?.Dispose();
            }

            _candidates.Clear();
            return plaintext;
        }

        // Every candidate is now one packet further along its keystream, so
        // none of them can be tried again against a later packet: a CTR cipher
        // has no way back. Say so once and stop.
        _logger.LogError(
            "no key candidate decrypts the mirror video to AVCC framing — tried {Count}; the stream cannot be read",
            _candidates.Count);
        _candidates.Clear();
        return null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        (_cipher as IDisposable)?.Dispose();
        foreach (var (_, candidate) in _candidates)
        {
            (candidate as IDisposable)?.Dispose();
        }

        _cts?.Dispose();
    }
}
