using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Claunia.PropertyList;
using Microsoft.Extensions.Logging;
using Streamsemble.AirPlay.Common;
using Streamsemble.AirPlay.Common.Hap;
using Streamsemble.AirPlay.Common.Video;
using Streamsemble.AirPlay.Sender.AirPlay2;
using Streamsemble.AirPlay.Sender.Raop;
using Streamsemble.Core.Video;

namespace Streamsemble.AirPlay.Sender.Video;

/// <summary>
/// One outbound screen-mirroring session: the hub playing the part a Mac plays,
/// against a TV that already accepts AirPlay mirroring.
///
/// The flow mirrors <see cref="AirPlay2Session"/>'s — RTSP, HAP pairing, an
/// encrypted control channel, a session SETUP and then a stream SETUP — with
/// three differences that matter:
/// <list type="bullet">
/// <item>The video stream is type 110 and its payload is H.264, passed through
///   from the Mac without re-encoding. We re-frame, re-encrypt and re-stamp; we
///   never touch the compressed bits. A companion type-96 audio stream rides in
///   the same SETUP, because a display that accepts one AirPlay session at a
///   time gets ALL of its media through this one — a separate speaker session
///   would reset the mirror.</item>
/// <item>Timing runs the other way. We serve a clock on
///   <see cref="MirrorNtpServer"/> and the TV polls it, which is why the session
///   advertises NTP rather than PTP.</item>
/// <item>Video is sealed with the modern envelope — ChaCha20-Poly1305 keyed by
///   HKDF over the pair-verify secret ("DataStream-Output-Encryption-Key"),
///   AAD = the 128-byte header — the same scheme a Mac uses toward us. The
///   <c>shk</c> in the SETUP is vestigial: WinPlay sends one too and neither
///   end ever uses it for video (live-run lesson: encrypting with the legacy
///   shk-derived AES-CTR instead gets a session the TV accepts end-to-end and
///   then renders as a black screen).</item>
/// </list>
/// </summary>
public sealed class MirrorSenderSession(
    string displayName,
    IPAddress address,
    int rtspPort,
    MirrorNtpServer timing,
    ILogger logger) : IDisposable
{
    private readonly RtspClient _rtsp = new(logger);
    private readonly string _sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
    private readonly ulong _streamConnectionId = unchecked((ulong)Random.Shared.NextInt64());

    private MirrorDataStreamCipher? _cipher;
    private TcpClient? _dataChannel;
    private NetworkStream? _dataStream;
    private CancellationTokenSource? _feedbackCts;
    private VideoCodecConfig? _config;
    private AirPlay2EventChannel? _eventChannel;

    // The companion audio stream (type 96). Its key is ours to choose, like the
    // video's, but unlike the video's it IS the ChaCha20 key — no derivation —
    // and the packets ride UDP with the standard realtime envelope.
    private readonly ulong _audioStreamConnectionId = unchecked((ulong)Random.Shared.NextInt64());
    private readonly byte[] _audioKey = RandomNumberGenerator.GetBytes(32);
    private readonly uint _audioRtpBase = (uint)Random.Shared.Next();
    private AirPlay2AudioCipher? _audioCipher;
    private UdpClient? _audioSocket;
    private UdpClient? _audioControlSocket;
    private IPEndPoint? _audioEndpoint;
    private IPEndPoint? _audioControlEndpoint;
    private ushort _audioSeq = (ushort)Random.Shared.Next(ushort.MaxValue);
    private long _audioSamplesSent;
    private ulong _audioNonceCounter;
    private bool _audioSyncSent;
    private uint _lastAudioSyncRtp;
    private long _lastAudioSyncAudibleNanos;

    public string DisplayName { get; } = displayName;

    public IPAddress DeviceAddress { get; } = address;

    /// <summary>Cleared at any observed death so the group rebuilds the session.</summary>
    public bool IsAlive { get; private set; } = true;

    public string PairingMode { get; private set; } = "none";

    public long PacketsSent { get; private set; }

    public long BytesSent { get; private set; }

    /// <summary>Access units dropped because their deadline had already passed when they reached the wire.</summary>
    public long FramesDropped { get; private set; }

    /// <summary>Whether the display granted the companion audio stream — if not, it shows video with no sound.</summary>
    public bool CarriesAudio => _audioCipher is not null;

    public long AudioPacketsSent { get; private set; }

    public float? LastKnownVolume { get; private set; }

    /// <summary>
    /// Sets the display's volume. While it shows the mirrored screen this RTSP
    /// channel is the only AirPlay session the display holds, so a volume
    /// request has nowhere else to go — the speaker group cannot reach it.
    /// Same wire form as the audio sessions: SET_PARAMETER "volume: &lt;dB&gt;".
    /// </summary>
    public async Task<bool> SetVolumeAsync(float linear, CancellationToken ct)
    {
        var db = linear <= 0.001f ? -144.0 : -30.0 + linear * 30.0;
        var body = System.Text.Encoding.ASCII.GetBytes($"volume: {db:F6}\r\n");
        try
        {
            var response = await _rtsp.RequestAsync("SET_PARAMETER", ct, "text/parameters", body).ConfigureAwait(false);
            if (response.IsSuccess)
            {
                LastKnownVolume = Math.Clamp(linear, 0f, 1f);
            }

            return response.IsSuccess;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Name}: mirror SET_PARAMETER volume failed", DisplayName);
            return false;
        }
    }

    public async Task<float?> GetVolumeAsync(CancellationToken ct)
    {
        try
        {
            var body = System.Text.Encoding.ASCII.GetBytes("volume\r\n");
            var response = await _rtsp.RequestAsync("GET_PARAMETER", ct, "text/parameters", body).ConfigureAwait(false);
            if (!response.IsSuccess || VolumeParameters.ParseDb(response.Body) is not { } db)
            {
                return null;
            }

            LastKnownVolume = VolumeParameters.DbToLinear(db);
            return LastKnownVolume;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "{Name}: mirror GET_PARAMETER volume failed", DisplayName);
            return null;
        }
    }

    public async Task ConnectAsync(VideoCodecConfig config, CancellationToken ct)
    {
        _config = config;
        await _rtsp.ConnectAsync(address, rtspPort, ct).ConfigureAwait(false);
        _rtsp.Uri = $"rtsp://{_rtsp.LocalAddress}/{_sessionUuid}";
        _rtsp.DefaultHeaders["User-Agent"] = "AirPlay/665.13";
        _rtsp.DefaultHeaders["Client-Instance"] = RandomHex(16);
        _rtsp.DefaultHeaders["DACP-ID"] = RandomHex(16);
        _rtsp.DefaultHeaders["Active-Remote"] = ((uint)Random.Shared.Next()).ToString();

        var pairing = await AirPlay2Pairing.PairAsync(_rtsp, address, DisplayName, logger, ct).ConfigureAwait(false);
        PairingMode = pairing.Mode;
        var keys = HapSessionKeys.Derive(pairing.SharedSecret, controllerRole: true);
        await _rtsp.UpgradeStreamAsync(inner => new HapCipherStream(inner, keys.ControlReadKey, keys.ControlWriteKey), ct)
            .ConfigureAwait(false);
        logger.LogInformation("{Name}: mirror control channel encrypted ({Mode} pairing)", DisplayName, pairing.Mode);
        _eventChannel = new AirPlay2EventChannel(DisplayName, address, keys, logger)
        {
            OnClosed = () =>
            {
                logger.LogWarning("{Name}: mirror event channel closed — session is dead", DisplayName);
                IsAlive = false;
            },
        };

        // The video cipher comes from the pairing, not from anything in the
        // SETUP: HKDF over the shared secret we just established, salted with
        // the stream connection id. The 16-byte shk still travels in the SETUP
        // because real senders send one — but nobody derives anything from it.
        var streamKey = RandomNumberGenerator.GetBytes(16);
        _cipher = MirrorDataStreamCipher.Create(
            pairing.SharedSecret, _streamConnectionId, "DataStream-Output-Encryption-Key");

        timing.Start();
        await SetupSessionAsync(ct).ConfigureAwait(false);
        var ports = await SetupStreamsAsync(streamKey, ct).ConfigureAwait(false);
        await OpenDataChannelAsync(ports.VideoDataPort, ct).ConfigureAwait(false);
        OpenAudioStream(ports);

        // RECORD is the render gate. Without it a receiver completes the whole
        // negotiation, accepts the data connection, and then sits showing
        // "connected" while every packet we send is ignored — it has never been
        // told the stream started. The audio path learned the same lesson the
        // hard way; see AirPlay2Session's RECORD-early note.
        await SendRecordAsync(ct).ConfigureAwait(false);

        // The parameter sets go out before any picture; a decoder that gets a
        // slice first has nothing to decode it against.
        await SendCodecConfigAsync(config, ct).ConfigureAwait(false);

        _feedbackCts = new CancellationTokenSource();
        _ = FeedbackLoopAsync(_feedbackCts.Token);
        _ = HeartbeatLoopAsync(_feedbackCts.Token);
        logger.LogInformation("{Name}: mirroring {Config} to TCP :{Port} ({Audio})",
            DisplayName, config.Describe(), ports.VideoDataPort,
            CarriesAudio ? "carrying audio" : "video only");
    }

    private async Task SetupSessionAsync(CancellationToken ct)
    {
        var session = new NSDictionary
        {
            { "deviceID", new NSString("9F:D7:AF:12:34:56") },
            { "sessionUUID", new NSString(_sessionUuid) },
            { "name", new NSString("Streamsemble") },
            { "model", new NSString("Streamsemble1,1") },
            { "sourceVersion", new NSString("665.13") },
            // NTP, not PTP: mirror receivers pull time from the sender, and our
            // MirrorNtpServer is what answers on this port.
            { "timingProtocol", new NSString("NTP") },
            { "timingPort", new NSNumber(timing.Port) },
        };

        var reply = await RequestPlistAsync("SETUP", session, ct).ConfigureAwait(false);
        logger.LogInformation("{Name}: mirror session SETUP ok — keys: {Keys}", DisplayName, string.Join(", ", reply.Keys));

        // The receiver announced its reverse event channel; connect it before
        // any stream SETUP. A display with no live event channel completes the
        // whole negotiation and then never answers RECORD — it is waiting for
        // this connection, and it will reap the session over its absence.
        if (reply.TryGetValue("eventPort", out var eventPortObj) && eventPortObj is NSNumber eventPort)
        {
            await _eventChannel!.ConnectAsync((int)eventPort.ToLong(), ct).ConfigureAwait(false);
        }
    }

    private sealed record StreamPorts(int VideoDataPort, int? AudioDataPort, int? AudioControlPort);

    private async Task<StreamPorts> SetupStreamsAsync(byte[] streamKey, CancellationToken ct)
    {
        // Bound before SETUP because the request has to state where the
        // receiver may send audio control traffic back.
        _audioControlSocket = BindUdp();
        var audioControlPort = ((IPEndPoint)_audioControlSocket.Client.LocalEndPoint!).Port;

        var video = new NSDictionary
        {
            { "type", new NSNumber(110) },
            { "streamConnectionID", new NSNumber(unchecked((long)_streamConnectionId)) },
            { "shk", new NSData(streamKey) },
            // How deep a video buffer the display keeps. Real senders declare
            // 100 ms and put frames on the wire that close to their stamps —
            // the deep buffering lives on OUR side of the wire (the video
            // group holds frames until one send-lead before render), because
            // a mirror display is a realtime renderer, not a buffered one.
            { "latencyMs", new NSNumber(100) },
        };

        // The companion audio stream, in the SAME SETUP: one session, one
        // clock, so the display keeps sound and picture in lock-step itself.
        // Shape per WinPlay's MirrorSession (proven against tvOS).
        var audio = new NSDictionary
        {
            { "type", new NSNumber(96) },
            { "streamConnectionID", new NSNumber(unchecked((long)_audioStreamConnectionId)) },
            { "ct", new NSNumber(2) },                 // ALAC
            { "spf", new NSNumber(352) },
            { "sr", new NSNumber(44100) },
            { "audioFormat", new NSNumber(0x40000) },  // ALAC/44100/16/2
            { "audioFormatIndex", new NSNumber(0x12) },
            { "controlPort", new NSNumber(audioControlPort) },
            { "audioMode", new NSString("default") },
            { "usingScreen", new NSNumber(true) },
            { "latencyMin", new NSNumber(11025) },
            { "latencyMax", new NSNumber(88200) },
            { "shk", new NSData(_audioKey) },
            { "isMedia", new NSNumber(true) },
            { "supportsDynamicStreamID", new NSNumber(true) },
        };

        var reply = await RequestPlistAsync("SETUP", new NSDictionary { { "streams", new NSArray(video, audio) } }, ct)
            .ConfigureAwait(false);
        logger.LogInformation("{Name}: mirror stream SETUP reply: {Plist}", DisplayName, reply.ToXmlPropertyList());

        if (!reply.TryGetValue("streams", out var streamsObj) || streamsObj is not NSArray { Count: > 0 } streams)
        {
            throw new IOException($"{DisplayName}: mirror stream SETUP returned no streams");
        }

        var replyStreams = streams.OfType<NSDictionary>().ToList();
        var videoReply = replyStreams.FirstOrDefault(s => StreamType(s) == 110) ?? replyStreams.FirstOrDefault();
        if (videoReply is null || Port(videoReply, "dataPort") is not { } videoDataPort)
        {
            throw new IOException($"{DisplayName}: mirror stream SETUP returned no dataPort");
        }

        var audioReply = replyStreams.FirstOrDefault(s => StreamType(s) == 96);
        return new StreamPorts(videoDataPort, audioReply is null ? null : Port(audioReply, "dataPort"),
            audioReply is null ? null : Port(audioReply, "controlPort"));

        static long? StreamType(NSDictionary stream)
            => stream.TryGetValue("type", out var type) && type is NSNumber n ? n.ToLong() : null;

        static int? Port(NSDictionary stream, string key)
            => stream.TryGetValue(key, out var value) && value is NSNumber n ? (int)n.ToLong() : null;
    }

    /// <summary>
    /// Arms the audio half if the display granted it. A refusal is survivable —
    /// the mirror stays video-only, which is exactly what it was before the
    /// companion stream existed — but it means a silent display, so it is
    /// logged loudly.
    /// </summary>
    private void OpenAudioStream(StreamPorts ports)
    {
        if (ports.AudioDataPort is not { } audioDataPort)
        {
            logger.LogWarning(
                "{Name}: display did not grant the companion audio stream — it will show video with NO sound",
                DisplayName);
            _audioControlSocket?.Dispose();
            _audioControlSocket = null;
            return;
        }

        _audioSocket = BindUdp();
        _audioEndpoint = new IPEndPoint(address, audioDataPort);
        _audioControlEndpoint = new IPEndPoint(address, ports.AudioControlPort ?? audioDataPort);
        _audioCipher = new AirPlay2AudioCipher(_audioKey);
        logger.LogInformation("{Name}: mirror audio stream granted (data :{Data}, control :{Control})",
            DisplayName, audioDataPort, _audioControlEndpoint.Port);
    }

    private static UdpClient BindUdp()
    {
        var socket = new UdpClient(AddressFamily.InterNetworkV6);
        socket.Client.DualMode = true;
        socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return socket;
    }

    private async Task OpenDataChannelAsync(int dataPort, CancellationToken ct)
    {
        _dataChannel = new TcpClient(address.AddressFamily) { NoDelay = true };
        await _dataChannel.ConnectAsync(address, dataPort, ct).ConfigureAwait(false);
        _dataStream = _dataChannel.GetStream();
    }

    private async Task SendRecordAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var response = await _rtsp.RequestAsync("RECORD", timeout.Token, extraHeaders: new Dictionary<string, string>
            {
                ["Range"] = "npt=0-",
                ["RTP-Info"] = "seq=0;rtptime=0",
                ["X-Apple-ProtocolVersion"] = "1",
            }).ConfigureAwait(false);
            logger.LogInformation("{Name}: mirror RECORD {Status} {Reason}",
                DisplayName, response.StatusCode, response.ReasonPhrase);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "{Name}: no mirror RECORD response within 5s — the display may never start rendering", DisplayName);
        }
    }

    /// <summary>
    /// Sends the parameter sets. This packet is deliberately NOT encrypted —
    /// the codec config is the one payload the protocol carries in the clear,
    /// and a receiver handed an encrypted one sees a malformed avcC and shows
    /// nothing, with no error to explain it. Its header carries the encode and
    /// display dimensions; the TV configures its render pipeline from them.
    /// </summary>
    public async Task SendCodecConfigAsync(VideoCodecConfig config, CancellationToken ct)
    {
        _config = config;
        var payload = config.ToAvcC();
        var header = MirrorSenderHeaders.CodecConfig(
            payload.Length, MirrorNtpServer.Ntp(Timing.Ptp.PtpReceiverClock.NowNanos),
            config.Width, config.Height, config.Width, config.Height);
        await SendPacketAsync(header, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ships one access unit, stamped with the instant it should be on screen.
    /// The stamp is the source's own deadline carried straight through from the
    /// inbound stream, so the TV renders on the same instant the speakers are
    /// rendering the sound that goes with it.
    /// </summary>
    public async Task SendFrameAsync(VideoFrame frame, CancellationToken ct)
    {
        if (_cipher is null || _dataStream is null)
        {
            return;
        }

        // The TV's mirror decoder is configured from the out-of-band avcC codec
        // packet and expects each video packet to carry coded slices ONLY. Our
        // inbound access units carry SPS/PPS inline (the assembler even injects
        // them ahead of keyframes), and forwarding those verbatim leaves the TV
        // showing nothing while its audio plays fine — strip to VCL, exactly as
        // real senders do. A config-only unit strips to empty and is skipped.
        byte[] plaintext;
        try
        {
            plaintext = H264Nal.VclOnly(frame.Data.Span);
        }
        catch (InvalidDataException ex)
        {
            logger.LogDebug(ex, "{Name}: dropping a malformed access unit", DisplayName);
            return;
        }

        if (plaintext.Length == 0)
        {
            return;
        }

        // The header is the AAD, so it exists before the payload does — and
        // its length field counts the tag the sealing is about to append.
        var header = MirrorSenderHeaders.Video(
            plaintext.Length + _cipher.Overhead,
            frame.IsKeyframe,
            MirrorPacketHeader.UnixNanosToNtp(frame.TargetNanos));
        var payload = _cipher.Seal(header, plaintext);
        await SendPacketAsync(header, payload, ct).ConfigureAwait(false);
    }

    /// <summary>Keeps an idle session from being reaped when the screen is static.</summary>
    public Task SendHeartbeatAsync(CancellationToken ct) =>
        SendPacketAsync(
            MirrorSenderHeaders.Heartbeat(MirrorNtpServer.Ntp(Timing.Ptp.PtpReceiverClock.NowNanos)), [], ct);

    /// <summary>
    /// A static screen produces no frames, and a display that hears nothing on
    /// the data channel for a while reaps the session (real senders heartbeat
    /// every second). Fires only when no real packet went out in the last one.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && IsAlive)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Environment.TickCount64 - Volatile.Read(ref _lastDataSendTicks) >= 1000)
            {
                await SendHeartbeatAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private long _lastDataSendTicks;

    /// <summary>
    /// Ships one PCM frame on the companion audio stream. The caller (the
    /// speaker group's send loop) has already paced it, so it goes straight to
    /// the wire; <paramref name="audibleNanos"/> is the grandmaster instant the
    /// frame turns audible on the group timeline, and the sync packets map the
    /// RTP counter onto exactly that — the display renders each sample when
    /// the speakers do. A no-op when the display granted no audio stream.
    /// </summary>
    public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, long audibleNanos, CancellationToken ct)
    {
        if (_audioCipher is null || _audioSocket is null || _audioEndpoint is null)
        {
            return;
        }

        var rtpTime = (uint)(_audioRtpBase + _audioSamplesSent);
        await MaybeSendAudioSyncAsync(rtpTime, audibleNanos, ct).ConfigureAwait(false);

        var packet = MirrorAudioPacket.Build(pcm.Span, _audioSeq, rtpTime, _audioNonceCounter, _audioCipher);
        try
        {
            await _audioSocket.SendAsync(packet, _audioEndpoint, ct).ConfigureAwait(false);
            AudioPacketsSent++;
            BytesSent += packet.Length;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // UDP to a live session shouldn't fail; if it does, the video
            // path's TCP writes will notice the death and mark the session.
            logger.LogDebug(ex, "{Name}: mirror audio send failed", DisplayName);
        }

        // The timeline advances even past a failed send — a hole in the
        // stream must stay a hole, not shift every later sample.
        _audioNonceCounter++;
        _audioSeq++;
        _audioSamplesSent += pcm.Length / 4;
    }

    /// <summary>
    /// Re-states the RTP→audible mapping once a second, and immediately when
    /// it jumps: a source pause advances audible time while the RTP counter
    /// stands still, so the first frame after a resume carries a new mapping
    /// that must reach the display before the samples scheduled on it.
    /// </summary>
    private async ValueTask MaybeSendAudioSyncAsync(uint rtpTime, long audibleNanos, CancellationToken ct)
    {
        var predicted = _lastAudioSyncAudibleNanos + (long)(rtpTime - _lastAudioSyncRtp) * 1_000_000_000L / 44100;
        var drifted = Math.Abs(audibleNanos - predicted) > 15_000_000;
        if (_audioSyncSent && !drifted && rtpTime - _lastAudioSyncRtp < 44100)
        {
            return;
        }

        var packet = MirrorAudioPacket.BuildSync(
            rtpTime, audibleNanos, Timing.Ptp.PtpReceiverClock.NowNanos, first: !_audioSyncSent);
        try
        {
            await _audioControlSocket!.SendAsync(packet, _audioControlEndpoint!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "{Name}: mirror audio sync send failed", DisplayName);
        }

        _audioSyncSent = true;
        _lastAudioSyncRtp = rtpTime;
        _lastAudioSyncAudibleNanos = audibleNanos;
    }

    private async Task SendPacketAsync(byte[] header, byte[] payload, CancellationToken ct)
    {
        if (_dataStream is not { } stream)
        {
            return;
        }

        var packet = new byte[MirrorPacketHeader.Length + payload.Length];
        header.CopyTo(packet, 0);
        payload.CopyTo(packet, MirrorPacketHeader.Length);

        try
        {
            await stream.WriteAsync(packet, ct).ConfigureAwait(false);
            PacketsSent++;
            BytesSent += packet.Length;
            Volatile.Write(ref _lastDataSendTicks, Environment.TickCount64);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            logger.LogWarning(ex, "{Name}: mirror data channel write failed — session is dead", DisplayName);
            IsAlive = false;
        }
    }

    internal void NoteDroppedFrame() => FramesDropped++;

    /// <summary>Real senders post this every couple of seconds; receivers treat its absence as a dead session.</summary>
    private async Task FeedbackLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        var ticks = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                await _rtsp.RequestAsync("POST", ct, uriOverride: "/feedback").ConfigureAwait(false);
                failures = 0;

                // The display's volume can move under us (its own remote), and
                // nothing pushes that back — poll it on the session that owns
                // it. First read lands ~2 s after connect rather than during
                // it, keeping the RTT off the first-picture path.
                if (CarriesAudio && ticks++ % 3 == 0)
                {
                    await GetVolumeAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (++failures == 1)
                {
                    logger.LogWarning(ex, "{Name}: mirror /feedback failed", DisplayName);
                }

                if (failures > 5)
                {
                    IsAlive = false;
                    return;
                }
            }
        }
    }

    private async Task<NSDictionary> RequestPlistAsync(string method, NSDictionary body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        RtspResponse response;
        try
        {
            response = await _rtsp.RequestAsync(method, timeout.Token, "application/x-apple-binary-plist",
                BinaryPropertyListWriter.WriteToArray(body)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{DisplayName}: mirror {method} got no response within 8s");
        }

        if (response.Header("Session") is { } session && !_rtsp.DefaultHeaders.ContainsKey("Session"))
        {
            _rtsp.DefaultHeaders["Session"] = session.Split(';')[0].Trim();
        }

        if (!response.IsSuccess)
        {
            throw new IOException($"{DisplayName}: mirror {method} returned {response.StatusCode} {response.ReasonPhrase}");
        }

        return response.Body.Length > 0 && PropertyListParser.Parse(response.Body) is NSDictionary dict
            ? dict
            : new NSDictionary();
    }

    public async Task TeardownAsync(CancellationToken ct)
    {
        try
        {
            await _rtsp.RequestAsync("TEARDOWN", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "{Name}: mirror TEARDOWN failed", DisplayName);
        }
    }

    private static string RandomHex(int digits)
    {
        Span<byte> bytes = stackalloc byte[digits / 2];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    public void Dispose()
    {
        _feedbackCts?.Cancel();
        _dataStream?.Dispose();
        _dataChannel?.Dispose();
        _audioSocket?.Dispose();
        _audioControlSocket?.Dispose();
        _eventChannel?.Dispose();
        _rtsp.Dispose();
        _feedbackCts?.Dispose();
    }
}
