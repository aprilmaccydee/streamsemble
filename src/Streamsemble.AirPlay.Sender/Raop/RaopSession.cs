using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Streamsemble.Core.Metadata;

namespace Streamsemble.AirPlay.Sender.Raop;

/// <summary>
/// One RTSP session with one RAOP (AirPlay v1 audio) speaker: ANNOUNCE →
/// SETUP → RECORD, then volume/metadata via SET_PARAMETER while the group
/// streams RTP to <see cref="AudioEndpoint"/>.
/// </summary>
public sealed class RaopSession(string displayName, IPAddress address, int rtspPort, bool encrypted, int latencyTrimMs, ILogger logger)
    : ITargetSession
{
    private readonly RtspClient _rtsp = new(logger);
    private byte[] _aesKey = [];
    private byte[] _aesIv = [];
    private uint _lastKnownRtpTime;

    public string DisplayName { get; } = displayName;
    public bool Encrypted { get; } = encrypted;
    public int LatencyTrimMs { get; } = latencyTrimMs;
    public IPAddress DeviceAddress { get; } = address;
    public bool RequiresPtp => false;
    public IPEndPoint AudioEndpoint { get; private set; } = new(address, 0);
    public IPEndPoint ControlEndpoint { get; private set; } = new(address, 0);
    public IPEndPoint TimingEndpoint { get; private set; } = new(address, 0);

    /// <summary>Builds the RAOP wire datagram: RTP header + ALAC-verbatim payload (AES-CBC-encrypted when the device wants it).</summary>
    public byte[] PrepareWirePacket(byte[] rtpHeader, byte[] pcm, ushort sequenceNumber, uint rtpTimestamp)
    {
        var alac = new byte[AlacPacker.PackedLength(pcm.Length / 4)];
        var alacLength = AlacPacker.Pack(pcm, alac);

        var packet = new byte[12 + alacLength];
        rtpHeader.CopyTo(packet, 0);
        alac.AsSpan(0, alacLength).CopyTo(packet.AsSpan(12));

        if (Encrypted)
        {
            var payload = packet.AsSpan(12);
            var encryptable = payload.Length / 16 * 16;
            if (encryptable > 0)
            {
                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key = _aesKey;
                aes.EncryptCbc(payload[..encryptable], _aesIv, payload[..encryptable], System.Security.Cryptography.PaddingMode.None);
            }
        }

        return packet;
    }

    /// <summary>Latency the device reported in the RECORD response, in samples (0 if none).</summary>
    public uint ReportedLatency { get; private set; }

    public async Task ConnectAsync(
        byte[]? aesKey,
        byte[]? aesIv,
        int ourControlPort,
        int ourTimingPort,
        ushort startSeq,
        uint startRtpTime,
        CancellationToken ct)
    {
        _aesKey = aesKey ?? [];
        _aesIv = aesIv ?? [];
        await _rtsp.ConnectAsync(address, rtspPort, ct).ConfigureAwait(false);

        var sessionId = (uint)Random.Shared.Next();
        _rtsp.Uri = $"rtsp://{address}/{sessionId}";
        _rtsp.DefaultHeaders["User-Agent"] = "iTunes/11.0.4 (Macintosh; OS X 10.8.3)";
        _rtsp.DefaultHeaders["Client-Instance"] = RandomHex(16);
        _rtsp.DefaultHeaders["DACP-ID"] = RandomHex(16);
        _rtsp.DefaultHeaders["Active-Remote"] = ((uint)Random.Shared.Next()).ToString();

        var sdp = new StringBuilder()
            .Append("v=0\r\n")
            .Append($"o=iTunes {sessionId} 0 IN IP4 {_rtsp.LocalAddress}\r\n")
            .Append("s=iTunes\r\n")
            .Append($"c=IN IP4 {address}\r\n")
            .Append("t=0 0\r\n")
            .Append("m=audio 0 RTP/AVP 96\r\n")
            .Append("a=rtpmap:96 AppleLossless\r\n")
            .Append("a=fmtp:96 352 0 16 40 10 14 2 255 0 0 44100\r\n");
        if (Encrypted)
        {
            sdp.Append($"a=rsaaeskey:{Convert.ToBase64String(AppleRsa.EncryptAesKey(aesKey!))}\r\n")
               .Append($"a=aesiv:{Convert.ToBase64String(aesIv!)}\r\n");
        }

        Ensure(await _rtsp.RequestAsync("ANNOUNCE", ct, "application/sdp", Encoding.ASCII.GetBytes(sdp.ToString())).ConfigureAwait(false), "ANNOUNCE");

        var setup = await _rtsp.RequestAsync("SETUP", ct, extraHeaders: new Dictionary<string, string>
        {
            ["Transport"] = $"RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port={ourControlPort};timing_port={ourTimingPort}",
        }).ConfigureAwait(false);
        Ensure(setup, "SETUP");
        ParseTransport(setup.Header("Transport") ?? throw new IOException($"{DisplayName}: SETUP response missing Transport header"));
        if (setup.Header("Session") is { } session)
        {
            _rtsp.DefaultHeaders["Session"] = session.Split(';')[0];
        }

        var record = await _rtsp.RequestAsync("RECORD", ct, extraHeaders: new Dictionary<string, string>
        {
            ["Range"] = "npt=0-",
            ["RTP-Info"] = $"seq={startSeq};rtptime={startRtpTime}",
        }).ConfigureAwait(false);
        Ensure(record, "RECORD");
        if (uint.TryParse(record.Header("Audio-Latency"), out var latency))
        {
            ReportedLatency = latency;
        }

        _lastKnownRtpTime = startRtpTime;
        logger.LogInformation(
            "{Name}: RAOP session up (audio :{Audio}, control :{Control}, timing :{Timing}, latency {Latency}, {Enc})",
            DisplayName, AudioEndpoint.Port, ControlEndpoint.Port, TimingEndpoint.Port, ReportedLatency, Encrypted ? "RSA/AES" : "unencrypted");
    }

    public void NoteRtpTime(uint rtpTime) => _lastKnownRtpTime = rtpTime;

    public async Task SetVolumeAsync(float linear, CancellationToken ct)
    {
        // RAOP volume is dB attenuation: -30 (quietest) … 0, with -144 = mute.
        var db = linear <= 0.001f ? -144.0 : -30.0 + linear * 30.0;
        var body = Encoding.ASCII.GetBytes($"volume: {db:F6}\r\n");
        Ensure(await _rtsp.RequestAsync("SET_PARAMETER", ct, "text/parameters", body).ConfigureAwait(false), "SET_PARAMETER volume");
    }

    public async Task SetMetadataAsync(TrackMetadata metadata, CancellationToken ct)
    {
        var body = Dmap.TrackItem(metadata);
        var response = await _rtsp.RequestAsync("SET_PARAMETER", ct, "application/x-dmap-tagged", body,
            new Dictionary<string, string> { ["RTP-Info"] = $"rtptime={_lastKnownRtpTime}" }).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            // Metadata is cosmetic; some receivers reject DMAP — never fail the stream over it.
            logger.LogDebug("{Name}: metadata rejected ({Status})", DisplayName, response.StatusCode);
        }
    }

    public async Task FlushAsync(ushort nextSeq, uint nextRtpTime, CancellationToken ct)
    {
        Ensure(await _rtsp.RequestAsync("FLUSH", ct, extraHeaders: new Dictionary<string, string>
        {
            ["RTP-Info"] = $"seq={nextSeq};rtptime={nextRtpTime}",
        }).ConfigureAwait(false), "FLUSH");
    }

    public async Task TeardownAsync(CancellationToken ct)
    {
        try
        {
            await _rtsp.RequestAsync("TEARDOWN", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "{Name}: TEARDOWN failed (already gone?)", DisplayName);
        }
    }

    private void ParseTransport(string transport)
    {
        foreach (var part in transport.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || !int.TryParse(kv[1], out var port))
            {
                continue;
            }

            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "server_port":
                    AudioEndpoint = new IPEndPoint(address, port);
                    break;
                case "control_port":
                    ControlEndpoint = new IPEndPoint(address, port);
                    break;
                case "timing_port":
                    TimingEndpoint = new IPEndPoint(address, port);
                    break;
            }
        }

        if (AudioEndpoint.Port == 0)
        {
            throw new IOException($"{DisplayName}: SETUP Transport did not include server_port ({transport})");
        }
    }

    private void Ensure(RtspResponse response, string what)
    {
        if (!response.IsSuccess)
        {
            throw new IOException($"{DisplayName}: {what} failed with {response.StatusCode} {response.ReasonPhrase}");
        }
    }

    private static string RandomHex(int digits)
    {
        Span<byte> bytes = stackalloc byte[digits / 2];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    public void Dispose() => _rtsp.Dispose();
}
