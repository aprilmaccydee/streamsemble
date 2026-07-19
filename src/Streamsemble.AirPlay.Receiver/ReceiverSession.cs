using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Claunia.PropertyList;
using Microsoft.Extensions.Logging;
using Streamsemble.AirPlay.Common.Hap;
using Streamsemble.AirPlay.Receiver.Audio;
using Streamsemble.AirPlay.Receiver.Rtsp;
using Streamsemble.Core.Audio;
using Streamsemble.Core.Metadata;
using Streamsemble.Timing.Ptp;

namespace Streamsemble.AirPlay.Receiver;

/// <summary>Stable identity the receiver presents in mDNS, /info, and pairing.</summary>
public sealed record ReceiverIdentity(string Name, string DeviceId, string Pi, byte[] PublicKey)
{
    public string PkHex => Convert.ToHexString(PublicKey).ToLowerInvariant();
}

/// <summary>
/// One AirPlay 2 sender's RTSP session against our receiver: transient
/// pair-setup → HAP-encrypted channel → SETUP (session, then buffered stream)
/// → RECORD/SETRATEANCHORTIME → AAC in, canonical PCM out to the source. The
/// flow and required replies mirror what a real Mac exchanged with a
/// transient-pairing receiver (debug/airplay-buffered/mac-to-sonos pcap):
/// GET /info → pair-setup (HKP 4, no fp-setup) → encrypted everything-else.
/// </summary>
public sealed class ReceiverSession(
    RtspServerConnection connection,
    AirPlayReceiverSource source,
    ReceiverIdentity identity,
    PtpReceiverClock ptp,
    ILogger logger) : IRtspConnectionHandler
{
    private readonly TransientPairSetupServer _pairSetup = new();
    private HapSessionKeys? _keys;
    private IPAddress? _ptpPeer; // captured at SETUP — the TCP socket is already disposed when we are

    private TcpListener? _eventListener;
    private BufferedAudioServer? _audioServer;
    private RealtimeAudioServer? _realtimeServer;
    private AacDecoderPipe? _decoder;
    private PacedPcmEmitter? _emitter;
    private UdpClient? _controlSocket;
    private CancellationTokenSource? _streamCts;
    private TrackMetadata _metadata = new();

    public Task<RtspReply> HandleAsync(RtspRequest request, CancellationToken ct)
    {
        var reply = (request.Method, request.Path) switch
        {
            ("GET" or "POST", "/info") => InfoReply(),
            ("POST", "/fp-setup") => FpSetupReply(request),
            ("POST", "/pair-setup") => PairSetupReply(request),
            ("POST", "/pair-verify") => PairVerifyReply(),
            ("POST", "/feedback") => RtspReply.Ok(),
            ("POST", "/command") => RtspReply.Ok(),
            ("POST", "/audioMode") => RtspReply.Ok(),
            ("OPTIONS", _) => OptionsReply(),
            ("SETUP", _) => SetupReply(request),
            ("SETPEERS" or "SETPEERSX", _) => RtspReply.Ok(),
            ("RECORD", _) => RecordReply(),
            ("SETRATEANCHORTIME", _) => RateAnchorReply(request),
            ("SET_PARAMETER", _) => SetParameterReply(request),
            ("GET_PARAMETER", _) => GetParameterReply(request),
            ("FLUSHBUFFERED" or "FLUSH", _) => FlushReply(),
            ("TEARDOWN", _) => TeardownReply(request),
            _ => Unhandled(request),
        };
        return Task.FromResult(reply);
    }

    private RtspReply Unhandled(RtspRequest request)
    {
        logger.LogWarning("RTSP {Method} {Path} not implemented (replying 501)", request.Method, request.Path);
        return RtspReply.Error(501, "Not Implemented");
    }

    // ---------------------------------------------------------------- /info

    private RtspReply InfoReply()
    {
        // Field set modeled on the Sonos /info reply a real Mac accepted right
        // before transient pairing. The features mask (bit 48 transient
        // pairing, bit 40 buffered audio) is what makes the Mac skip fp-setup
        // and go straight to pair-setup with X-Apple-HKP: 4.
        // Kitchen-Sonos /info (extracted plaintext from the mac-to-sonos pcap)
        // is the ground truth this mirrors — round 10 taught that "modeled on"
        // isn't "matching": our invented supportedFormats dict (absent on
        // Sonos) and missing audioStream/PTPInfo/featuresEx left the Mac
        // clock-synced and session-happy but never opening the audio stream.
        // Identity fields and the features mask stay ours (bit 26→14 swap is
        // deliberate: MFi we can't sign, FairPlay we can).
        var info = new NSDictionary
        {
            { "deviceID", new NSString(identity.DeviceId) },
            { "features", new NSNumber(unchecked((long)ReceiverFeatures.Mask)) },
            { "featuresEx", new NSString(ReceiverFeatures.FeaturesEx) },
            { "name", new NSString(identity.Name) },
            { "nameIsFactoryDefault", new NSNumber(false) },
            { "manufacturer", new NSString("Streamsemble") },
            { "model", new NSString(ReceiverConstants.Model) },
            { "pi", new NSString(identity.Pi) },
            { "protocolVersion", new NSString("1.1") },
            { "sourceVersion", new NSString(ReceiverConstants.SourceVersion) },
            { "statusFlags", new NSNumber(4) },
            { "keepAliveLowPower", new NSNumber(true) },
            { "keepAliveSendStatsAsBody", new NSNumber(true) },
            // The modern-receiver surface, mirrored from the living-room TV
            // (sdk AirPlay;3.5.0.244): the Mac picks its protocol dialect from
            // these — with the old Sonos surface (AirPlay;2.7.1) it still sent
            // the 3.x streamConnections SETUP our replies never answered, and
            // its transport tore down its audio UDP channels unbound.
            { "OSInfo", new NSString("Linux 4.9.99") },
            { "PTPInfo", new NSString("AirPlay;3.5.0.244") },
            { "sdk", new NSString("AirPlay;3.5.0.244") },
            { "build", new NSString("40.00") },
            // Present on every receiver a Mac streams to (Sonos and TV alike).
            { "firmwareBuildDate", new NSString("May 16 2025") },
            { "firmwareRevision", new NSString("1.1.1") },
            { "hardwareRevision", new NSString("SH1M_WW_9972_20") },
            // NO format dicts at all, like the TV: neither supportedFormats
            // (rounds 10/12: its presence suppresses streaming entirely) nor
            // supportedAudioFormatsExtended (the TV lacks it; its shape never
            // influenced the Mac's stream-type choice in rounds 11-17 anyway —
            // system output computes ALAC realtime for every audio receiver
            // and Music uses buffered, decided sender-side).
            { "volumeControlType", new NSNumber(3) },
            { "txtAirPlay", new NSData(BuildTxtAirPlay()) },
        };
        return PlistReply(info);
    }

    private byte[] BuildTxtAirPlay()
    {
        var entries = ReceiverFeatures.TxtRecords(identity).Select(kv => $"{kv.Key}={kv.Value}");
        var bytes = new List<byte>();
        foreach (var entry in entries)
        {
            var encoded = Encoding.UTF8.GetBytes(entry);
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }

        return [.. bytes];
    }

    // ------------------------------------------------------------- pairing

    private RtspReply FpSetupReply(RtspRequest request)
    {
        // With the Sonos-style features mask real senders never ask for this,
        // but the responder costs nothing and covers stricter SDKs.
        var body = request.Body.Length switch
        {
            16 => FairPlaySetup.HandleSetupPhase1(request.Body),
            164 => FairPlaySetup.HandleSetupPhase2(request.Body),
            _ => throw new IOException($"unexpected fp-setup request length {request.Body.Length}"),
        };
        return new RtspReply { Body = body, ContentType = "application/octet-stream" };
    }

    private RtspReply PairSetupReply(RtspRequest request)
    {
        var body = _pairSetup.HandleMessage(request.Body);
        var reply = new RtspReply { Body = body, ContentType = "application/octet-stream" };
        if (_pairSetup.SharedSecret is { } secret)
        {
            _keys = HapSessionKeys.Derive(secret, controllerRole: false);
            logger.LogInformation("transient pair-setup complete; upgrading to encrypted channel");
            return reply with
            {
                UpgradeAfterSend = inner => new HapCipherStream(inner, _keys.ControlReadKey, _keys.ControlWriteKey),
            };
        }

        return reply;
    }

    private RtspReply PairVerifyReply()
    {
        // Transient-only receiver: nothing is ever persistently paired, so a
        // verify attempt means stale sender state. An Authentication error TLV
        // makes senders fall back to pair-setup.
        logger.LogInformation("pair-verify attempted against transient-only receiver; signalling authentication error");
        var body = new Tlv8()
            .AddState(2)
            .Add(TlvType.Error, (byte)PairingError.Authentication)
            .Encode();
        return new RtspReply { Body = body, ContentType = "application/octet-stream" };
    }

    private static RtspReply OptionsReply() => new()
    {
        Headers =
        {
            ["Public"] = "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, FLUSHBUFFERED, TEARDOWN, OPTIONS, " +
                         "POST, GET, PUT, SET_PARAMETER, GET_PARAMETER, SETPEERS, SETPEERSX, SETRATEANCHORTIME",
        },
    };

    // --------------------------------------------------------------- SETUP

    private RtspReply SetupReply(RtspRequest request)
    {
        if (PropertyListParser.Parse(request.Body) is not NSDictionary plist)
        {
            return RtspReply.Error(400, "Bad Request");
        }

        return plist.ContainsKey("streams") ? SetupStream(plist) : SetupSession(plist);
    }

    private RtspReply SetupSession(NSDictionary plist)
    {
        StartEventListener();
        logger.LogInformation("session SETUP ok (timingProtocol {Timing}); event port {Port}",
            plist.TryGetValue("timingProtocol", out var tp) ? tp.ToString() : "?",
            ((IPEndPoint)_eventListener!.LocalEndpoint).Port);

        // Ground truth for reply shapes: log what the Mac itself sends here.
        logger.LogDebug("SETUP session keys: {Keys}", string.Join(", ", plist.Keys));
        if (plist.TryGetValue("timingPeerInfo", out var peerInfo))
        {
            logger.LogDebug("sender timingPeerInfo: {Info}", peerInfo.ToXmlPropertyList());
        }

        // A Mac only streams once it accepts our clock as grandmaster, and its
        // PTP daemon speaks from/to ROUTABLE addresses (v4 in every working
        // capture) — round 9: serving our clock at the RTSP peer's v6
        // link-local while its daemon talked v4 meant our announces were never
        // associated with the receiver's clock, its BMCA never saw us, no
        // stream. Serve the routable address it declares in timingPeerInfo.
        //
        // Same-host senders are unsupported by construction: macOS's own
        // AirPlay daemon owns the 319/320 timing relationship, our bind steals
        // its packet delivery, and every address we could advertise is its own
        // (rounds 3-6 were all this machine streaming to itself). Not binding
        // also keeps the receiver from hijacking the sender role's PtpEngine
        // ports mid-cast and preserves the loopback WAV rig.
        if (IsOwnAddress(CleanAddress(connection.RemoteAddress)))
        {
            logger.LogWarning(
                "sender {Remote} is this machine — same-host AirPlay cannot form a PTP timing relationship, " +
                "so macOS will not hand audio over; test from a different device (iPhone/iPad/another Mac)",
                connection.RemoteAddress);
        }
        else if (ptp.EnsureStarted() && _ptpPeer is null)
        {
            _ptpPeer = SelectPeerTimingAddress(peerInfo as NSDictionary) ?? connection.RemoteAddress;
            ptp.AddPeer(_ptpPeer);
        }

        // timingPort is a dummy under PTP; timingPeerInfo mirrors goplay2's
        // Mac-proven reply shape: exactly ONE address and ID = that address.
        // The address must be ROUTABLE from the sender — the whole real-Mac
        // round 3/4/5 arc: a zone-scoped "fe80::…%14" string fails their parse
        // (harmlessly ignored, round 3), but a de-scoped bare link-local
        // parses and then can't be dialed, and the Mac aborts the session
        // right after this reply without ever sending PTP (rounds 4/5). So:
        // the IPv4 of the interface the RTSP connection arrived on.
        var local = SelectTimingAddress();
        var reply = new NSDictionary
        {
            { "eventPort", new NSNumber(((IPEndPoint)_eventListener.LocalEndpoint).Port) },
            { "timingPort", new NSNumber(0) },
            { "timingPeerInfo", new NSDictionary
                {
                    { "Addresses", new NSArray(new NSString(local)) },
                    { "ID", new NSString(local) },
                }
            },
        };
        logger.LogDebug("timingPeerInfo address advertised: {Address}", local);
        return PlistReply(reply);
    }

    /// <summary>
    /// The timing address we advertise: the IPv4 (else global v6) of the
    /// interface the RTSP connection arrived on; the connection address itself
    /// only as a last resort (it's a useless bare link-local when the sender
    /// connected over v6 LL, but better than nothing).
    /// </summary>
    private string SelectTimingAddress()
    {
        var local = CleanAddress(connection.LocalAddress);
        if (local.AddressFamily == AddressFamily.InterNetwork)
        {
            return local.ToString();
        }

        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    continue;
                }

                var addresses = nic.GetIPProperties().UnicastAddresses
                    .Select(u => CleanAddress(u.Address))
                    .ToList();
                if (!addresses.Contains(local))
                {
                    continue;
                }

                var best = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal);
                if (best is not null)
                {
                    return best.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "interface lookup for timing address failed");
        }

        return local.ToString();
    }

    private static IPAddress CleanAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            return new IPAddress(address.GetAddressBytes());
        }

        return address;
    }

    /// <summary>
    /// Where to serve our PTP clock: the sender's routable v4 (else global v6)
    /// from its declared timingPeerInfo.Addresses — the path its clock daemon
    /// actually uses. Link-locals are skipped (no zone → not dialable).
    /// </summary>
    private IPAddress? SelectPeerTimingAddress(NSDictionary? peerInfo)
    {
        if (peerInfo is null || !peerInfo.TryGetValue("Addresses", out var a) || a is not NSArray addresses)
        {
            return null;
        }

        var parsed = addresses.OfType<NSString>()
            .Select(s => IPAddress.TryParse(s.Content, out var ip) ? ip : null)
            .Where(ip => ip is not null && !ip.IsIPv6LinkLocal)
            .Cast<IPAddress>()
            .ToList();
        var best = parsed.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            ?? parsed.FirstOrDefault();
        if (best is not null)
        {
            logger.LogDebug("PTP peer address from sender timingPeerInfo: {Address}", best);
        }

        return best;
    }

    /// <summary>True when the (cleaned) address is one of this machine's own — a same-host sender.</summary>
    private static bool IsOwnAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Any(u => CleanAddress(u.Address).Equals(address));
        }
        catch
        {
            return false;
        }
    }

    private RtspReply SetupStream(NSDictionary plist)
    {
        var streams = (NSArray)plist["streams"];
        if (streams.Count == 0 || streams[0] is not NSDictionary stream)
        {
            return RtspReply.Error(400, "Bad Request");
        }

        var type = stream.TryGetValue("type", out var t) ? ((NSNumber)t).ToLong() : 0;
        switch (type)
        {
            case 103:
                break;
            case 96:
                return SetupRealtimeStream(stream);
            default:
                logger.LogWarning("stream SETUP type {Type} not supported", type);
                return RtspReply.Error(453, "Not Enough Bandwidth");
        }

        logger.LogDebug("buffered stream SETUP keys: {Keys}", string.Join(", ", stream.Keys));
        var compression = stream.TryGetValue("ct", out var c) ? ((NSNumber)c).ToLong() : 4;
        if (compression != 4)
        {
            logger.LogWarning("buffered stream ct {Ct} not supported (AAC-LC only)", compression);
            return RtspReply.Error(453, "Not Enough Bandwidth");
        }

        if (stream.TryGetValue("shk", out var shkObj) is false || shkObj is not NSData shk)
        {
            return RtspReply.Error(400, "Bad Request");
        }

        _streamCts = new CancellationTokenSource();
        _decoder = new AacDecoderPipe(logger);
        _emitter = new PacedPcmEmitter(_decoder.Pcm, pcm => source.PushDecodedPcm(pcm));
        _ = RunEmitterAsync(_emitter, _streamCts.Token);

        _audioServer = new BufferedAudioServer(shk.Bytes, logger)
        {
            OnPacket = (packet, ct) => _decoder.WriteFrameAsync(packet.Frame, ct),
        };
        _audioServer.Start();

        // Buffered senders expect a control port in the reply even though no
        // sync/retransmit traffic flows for type 103; hand out a real socket.
        // (DualMode must be set before the bind, so no bind-in-constructor.)
        _controlSocket = new UdpClient(AddressFamily.InterNetworkV6);
        _controlSocket.Client.DualMode = true;
        _controlSocket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));

        logger.LogInformation("buffered stream SETUP ok (data :{Data})", _audioServer.Port);
        var reply = new NSDictionary
        {
            { "streams", new NSArray(new NSDictionary
                {
                    { "streamID", new NSNumber(StreamId(stream)) },
                    { "type", new NSNumber(103) },
                    { "dataPort", new NSNumber(_audioServer.Port) },
                    { "controlPort", new NSNumber(((IPEndPoint)_controlSocket.Client.LocalEndPoint!).Port) },
                    { "audioBufferSize", new NSNumber(8 * 1024 * 1024) },
                })
            },
        };
        return PlistReply(reply);
    }

    /// <summary>
    /// Realtime (type 96) — what macOS system output sends (ALAC 44.1/16/2,
    /// 352-sample packets, same ChaCha20 envelope as buffered keyed by shk).
    /// There is no ANNOUNCE in AirPlay 2, so the ALAC config is synthesized
    /// from the protocol's fixed parameters. Rendered on arrival: the sender
    /// paces transmission, so no SETRATEANCHORTIME gate exists in this mode —
    /// the source goes active on the first decoded packet.
    /// </summary>
    private RtspReply SetupRealtimeStream(NSDictionary stream)
    {
        // Full dump — ground truth for the modern (AirPlay 3.x sdk) stream
        // negotiation: streamConnections/streamConnectionID arrived here long
        // before any reference receiver code handled them.
        logger.LogDebug("realtime stream SETUP: {Stream}", stream.ToXmlPropertyList());
        if (stream.TryGetValue("shk", out var shkObj) is false || shkObj is not NSData shk)
        {
            return RtspReply.Error(400, "Bad Request");
        }

        var streamId = StreamId(stream);

        var spf = stream.TryGetValue("spf", out var s) ? (int)((NSNumber)s).ToLong() : PcmFrame.SamplesPerFrame;
        var alac = new AlacDecoder(AlacSpecificConfig(spf));
        _streamCts = new CancellationTokenSource();

        var pcmBuffer = new byte[alac.MaxBytesPerPacket];
        var pending = new List<byte>(PcmFrame.CanonicalFrameBytes * 2);
        var active = false;
        _realtimeServer = new RealtimeAudioServer(shk.Bytes, logger)
        {
            OnPacket = (packet, _) =>
            {
                int written;
                try
                {
                    written = alac.DecodePacket(packet.Frame, pcmBuffer);
                }
                catch (InvalidDataException ex)
                {
                    logger.LogDebug(ex, "ALAC decode failed (seq {Seq})", packet.Sequence);
                    return ValueTask.CompletedTask;
                }

                if (!active)
                {
                    active = true;
                    source.MarkActive();
                    logger.LogInformation("realtime audio flowing (seq {Seq}, {Bytes} B PCM/packet)",
                        packet.Sequence, written);
                }

                // Full ALAC frames are exactly one canonical frame; the carry
                // list only ever holds a trailing partial.
                if (pending.Count == 0 && written == PcmFrame.CanonicalFrameBytes)
                {
                    source.PushDecodedPcm(pcmBuffer.AsSpan(0, written).ToArray());
                    return ValueTask.CompletedTask;
                }

                pending.AddRange(pcmBuffer.AsSpan(0, written).ToArray());
                var offset = 0;
                while (pending.Count - offset >= PcmFrame.CanonicalFrameBytes)
                {
                    var frame = new byte[PcmFrame.CanonicalFrameBytes];
                    pending.CopyTo(offset, frame, 0, frame.Length);
                    offset += frame.Length;
                    source.PushDecodedPcm(frame);
                }

                pending.RemoveRange(0, offset);
                return ValueTask.CompletedTask;
            },
        };
        _realtimeServer.Start();

        logger.LogInformation("realtime stream SETUP ok (data :{Data}, control :{Control})",
            _realtimeServer.DataPort, _realtimeServer.ControlPort);
        var replyStream = new NSDictionary
        {
            { "streamID", new NSNumber(streamId) },
            { "type", new NSNumber(96) },
            { "dataPort", new NSNumber(_realtimeServer.DataPort) },
            { "controlPort", new NSNumber(_realtimeServer.ControlPort) },
        };
        // Modern senders bind their transport channels to the stream by the
        // connection ID they proposed; echo it back if one arrived.
        if (stream.TryGetValue("streamConnectionID", out var scid))
        {
            replyStream.Add("streamConnectionID", scid);
        }

        // The modern (AirPlay 3.x sdk) transport reads ports from a
        // streamConnections reply mirroring its request — NOT from the legacy
        // dataPort/controlPort keys. Without this the sender creates its RTP
        // ("-gen") and RTCP ("-gnct") channels and finalizes them ~10 ms
        // later, unbound: engine streams into the void, no error anywhere.
        // Request shape: RTP {UseStreamEncryptionKey: true} (port wanted from
        // us), RTCP {Port: <sender's listen port>}.
        if (stream.TryGetValue("streamConnections", out var connections) && connections is NSDictionary requested)
        {
            if (requested.TryGetValue("streamConnectionTypeRTCP", out var rtcp)
                && rtcp is NSDictionary rtcpDict
                && rtcpDict.TryGetValue("streamConnectionKeyPort", out var senderRtcp))
            {
                logger.LogDebug("sender RTCP port {Port}", ((NSNumber)senderRtcp).ToLong());
            }

            replyStream.Add("streamConnections", new NSDictionary
            {
                { "streamConnectionTypeRTP", new NSDictionary
                    {
                        { "streamConnectionKeyPort", new NSNumber(_realtimeServer.DataPort) },
                        { "streamConnectionKeyUseStreamEncryptionKey", new NSNumber(true) },
                    }
                },
                { "streamConnectionTypeRTCP", new NSDictionary
                    {
                        { "streamConnectionKeyPort", new NSNumber(_realtimeServer.ControlPort) },
                    }
                },
            });
        }

        var reply = new NSDictionary
        {
            { "streams", new NSArray(replyStream) },
        };
        return PlistReply(reply);
    }

    /// <summary>
    /// The reply's streamID: echo the sender's if it names one, else assign 1.
    /// Reference receivers (shairport-sync, goplay2) always return one; the
    /// sender's transport binds its UDP channels to the stream by this ID.
    /// </summary>
    private static long StreamId(NSDictionary stream) =>
        stream.TryGetValue("streamID", out var sid) && sid is NSNumber n ? n.ToLong() : 1;

    /// <summary>The RAOP fmtp "352 0 16 40 10 14 2 255 0 0 44100" as a 24-byte ALACSpecificConfig.</summary>
    private static byte[] AlacSpecificConfig(int framesPerPacket)
    {
        var config = new byte[24];
        BinaryPrimitives.WriteInt32BigEndian(config, framesPerPacket);
        config[4] = 0;   // compatibleVersion
        config[5] = 16;  // bitDepth
        config[6] = 40;  // pb
        config[7] = 10;  // mb
        config[8] = 14;  // kb
        config[9] = 2;   // channels
        BinaryPrimitives.WriteUInt16BigEndian(config.AsSpan(10), 255); // maxRun
        BinaryPrimitives.WriteInt32BigEndian(config.AsSpan(20), 44100);
        return config;
    }

    private async Task RunEmitterAsync(PacedPcmEmitter emitter, CancellationToken ct)
    {
        try
        {
            await emitter.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PCM emitter failed");
        }
    }

    private void StartEventListener()
    {
        if (_eventListener is not null)
        {
            return;
        }

        _eventListener = new TcpListener(IPAddress.IPv6Any, 0);
        _eventListener.Server.DualMode = true;
        _eventListener.Start();
        _ = AcceptEventConnectionsAsync(_eventListener);
    }

    /// <summary>
    /// The sender connects into our event port. It's HAP-encrypted with the
    /// events keys and mostly idle; we 200 anything that arrives (mirror of
    /// the sender's ReadEventChannelAsync, roles swapped).
    /// </summary>
    private async Task AcceptEventConnectionsAsync(TcpListener listener)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = ServeEventConnectionAsync(client);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeEventConnectionAsync(TcpClient client)
    {
        logger.LogDebug("event channel connected from {Remote}", client.Client.RemoteEndPoint);
        try
        {
            using var _ = client;
            if (_keys is null)
            {
                return;
            }

            Stream stream = new HapCipherStream(client.GetStream(), _keys.EventsReadKey, _keys.EventsWriteKey);
            var buffer = new byte[2048];
            var pending = new List<byte>();
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                pending.AddRange(buffer.AsSpan(0, read).ToArray());
                while (TryTakeEventRequest(pending, out var cseq, out var requestLine))
                {
                    logger.LogDebug("event channel ← {Request}", requestLine);
                    var response = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Length: 0\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static bool TryTakeEventRequest(List<byte> pending, out string cseq, out string requestLine)
    {
        cseq = "0";
        requestLine = "";
        var bytes = pending.ToArray();
        var text = Encoding.ASCII.GetString(bytes);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
        {
            return false;
        }

        var header = text[..headerEnd];
        requestLine = header.Split("\r\n")[0];
        var contentLength = 0;
        foreach (var line in header.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
            }
            else if (line.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
            {
                cseq = line["CSeq:".Length..].Trim();
            }
        }

        var total = headerEnd + 4 + contentLength;
        if (bytes.Length < total)
        {
            return false;
        }

        pending.RemoveRange(0, total);
        return true;
    }

    // ----------------------------------------------------- playback control

    private RtspReply RecordReply()
    {
        logger.LogInformation("RECORD — sender is starting the stream");
        return new RtspReply { Headers = { ["Audio-Latency"] = "0" } };
    }

    private RtspReply RateAnchorReply(RtspRequest request)
    {
        if (PropertyListParser.Parse(request.Body) is not NSDictionary plist)
        {
            return RtspReply.Error(400, "Bad Request");
        }

        var rate = plist.TryGetValue("rate", out var r) ? ((NSNumber)r).ToLong() : 0;
        logger.LogInformation("SETRATEANCHORTIME rate={Rate}", rate);
        if (rate >= 1)
        {
            _emitter?.Go();
            source.MarkActive();
        }

        return RtspReply.Ok();
    }

    private RtspReply SetParameterReply(RtspRequest request)
    {
        var contentType = request.Header("Content-Type") ?? "";
        if (contentType.Contains("dmap", StringComparison.OrdinalIgnoreCase))
        {
            if (DmapMetadata.Parse(request.Body) is { } parsed)
            {
                _metadata = _metadata with { Title = parsed.Title, Artist = parsed.Artist, Album = parsed.Album };
                source.PushMetadata(_metadata);
                logger.LogInformation("now playing: {Artist} — {Title}", parsed.Artist, parsed.Title);
            }
        }
        else if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            _metadata = _metadata with { Artwork = request.Body, ArtworkMimeType = contentType };
            source.PushMetadata(_metadata);
        }
        else if (contentType.Contains("text/parameters", StringComparison.OrdinalIgnoreCase))
        {
            var text = Encoding.ASCII.GetString(request.Body).Trim();
            if (text.StartsWith("volume:", StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately observe-only: the hub never forwards sender
                // volume to the speaker fan-out unless the user asks.
                logger.LogInformation("sender volume {Volume} noted (not applied)", text["volume:".Length..].Trim());
            }
        }

        return RtspReply.Ok();
    }

    private static RtspReply GetParameterReply(RtspRequest request)
    {
        var wanted = Encoding.ASCII.GetString(request.Body).Trim();
        if (wanted.Equals("volume", StringComparison.OrdinalIgnoreCase))
        {
            return new RtspReply
            {
                ContentType = "text/parameters",
                Body = Encoding.ASCII.GetBytes("volume: 0.000000\r\n"),
            };
        }

        return RtspReply.Ok();
    }

    private RtspReply FlushReply()
    {
        // Drop whatever is queued for the pace loop; the sender re-anchors after.
        while (_decoder?.Pcm.TryRead(out _) == true)
        {
        }

        logger.LogInformation("FLUSHBUFFERED — queued audio dropped");
        return RtspReply.Ok();
    }

    private RtspReply TeardownReply(RtspRequest request)
    {
        var streamOnly = request.Body.Length > 0
            && PropertyListParser.Parse(request.Body) is NSDictionary plist
            && plist.ContainsKey("streams");
        logger.LogInformation("TEARDOWN ({Scope})", streamOnly ? "stream" : "session");
        StopStream();
        return RtspReply.Ok();
    }

    private void StopStream()
    {
        _streamCts?.Cancel();
        _audioServer?.Dispose();
        _realtimeServer?.Dispose();
        _decoder?.Dispose();
        _controlSocket?.Dispose();
        _audioServer = null;
        _realtimeServer = null;
        _decoder = null;
        _emitter = null;
        _controlSocket = null;
        source.MarkIdle();
    }

    private static RtspReply PlistReply(NSDictionary dict) => new()
    {
        ContentType = "application/x-apple-binary-plist",
        Body = BinaryPropertyListWriter.WriteToArray(dict),
    };

    public void Dispose()
    {
        StopStream();
        if (_ptpPeer is not null)
        {
            ptp.RemovePeer(_ptpPeer);
        }

        _eventListener?.Stop();
        _streamCts?.Dispose();
        logger.LogDebug("receiver session disposed ({Remote})", connection.RemoteAddress);
    }
}

/// <summary>Minimal DMAP (DAAP) parser for inbound now-playing metadata (mlit{minm,asar,asal}).</summary>
internal static class DmapMetadata
{
    public sealed record Parsed(string? Title, string? Artist, string? Album);

    public static Parsed? Parse(byte[] body)
    {
        string? title = null, artist = null, album = null;
        Walk(body, 0, body.Length);
        return title is null && artist is null && album is null ? null : new Parsed(title, artist, album);

        void Walk(byte[] data, int offset, int end)
        {
            while (offset + 8 <= end)
            {
                var code = Encoding.ASCII.GetString(data, offset, 4);
                var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 4));
                var valueStart = offset + 8;
                if (length < 0 || valueStart + length > end)
                {
                    return;
                }

                switch (code)
                {
                    case "mlit" or "mlog" or "mcon":
                        Walk(data, valueStart, valueStart + length);
                        break;
                    case "minm":
                        title = Encoding.UTF8.GetString(data, valueStart, length);
                        break;
                    case "asar":
                        artist = Encoding.UTF8.GetString(data, valueStart, length);
                        break;
                    case "asal":
                        album = Encoding.UTF8.GetString(data, valueStart, length);
                        break;
                }

                offset = valueStart + length;
            }
        }
    }
}
