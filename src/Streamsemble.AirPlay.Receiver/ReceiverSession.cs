using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Claunia.PropertyList;
using Microsoft.Extensions.Logging;
using Streamsemble.AirPlay.Common.Hap;
using Streamsemble.AirPlay.Common.FairPlay;
using Streamsemble.AirPlay.Common.Video;
using Streamsemble.AirPlay.Receiver.Audio;
using Streamsemble.AirPlay.Receiver.Rtsp;
using Streamsemble.AirPlay.Receiver.Video;
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
    MirrorVideoSource videoSource,
    ReceiverIdentity identity,
    PtpReceiverClock ptp,
    int presentationLatencySamples,
    MirrorDisplay display,
    ILogger logger) : IRtspConnectionHandler
{
    private readonly TransientPairSetupServer _pairSetup = new();
    private HapSessionKeys? _keys;
    private IPAddress? _ptpPeer; // captured at SETUP — the TCP socket is already disposed when we are

    private TcpListener? _eventListener;
    private BufferedAudioServer? _audioServer;
    private RealtimeAudioServer? _realtimeServer;
    private MirrorDataServer? _mirrorServer;
    private MirrorNtpClient? _mirrorTiming;
    private AacDecoderPipe? _decoder;
    private AacRtpPcmMapper? _aacPcmMapper;
    private PacedPcmEmitter? _emitter;
    private AnchoredPcmScheduler? _scheduler;
    private UdpClient? _controlSocket;
    private CancellationTokenSource? _streamCts;
    private TrackMetadata _metadata = new();

    /// <summary>
    /// The 164-byte fp-setup phase-2 body, retained because a FairPlay-keyed
    /// stream SETUP's <c>ekey</c> is only meaningful against it. Kept even
    /// though senders steered to HAP never send one — the cost is 164 bytes and
    /// the alternative is discovering mid-SETUP that the material is gone.
    /// </summary>
    private byte[]? _fairPlayKeyMessage;

    /// <summary>
    /// The sender's timing arrangement, captured at session SETUP. Under PTP
    /// the hub IS the grandmaster, so a mirror packet's presentation stamp is
    /// already on our timeline; under NTP the stamps are on the sender's clock
    /// and have to be polled for. Getting this wrong does not fail loudly — it
    /// silently offsets every video frame by the gap between two clocks.
    /// </summary>
    private bool _senderUsesPtpTiming;
    private int _senderTimingPort;
    private bool _senderIsMirroring;

    /// <summary>
    /// The FairPlay-wrapped stream key, when a sender sends one. It arrives in
    /// the SESSION SETUP rather than the stream SETUP — the one place it is
    /// easy to look past, since every other key on this connection travels with
    /// the stream it belongs to. A transiently-paired macOS mirror sends none.
    /// </summary>
    private byte[]? _sessionEncryptedKey;

    /// <summary>The session SETUP's <c>eiv</c> — the CBC IV a mirror's companion audio uses.</summary>
    private byte[]? _sessionEncryptedIv;

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
            // TV-exact, ZEROS INCLUDED (tv-groupfields.log). Zero declared
            // latency is the truth now: anchored render times (0xD7 /
            // SETRATEANCHORTIME) are honored by emitting one group latency
            // early, so audio is AUDIBLE at the sender's stated time — the
            // same contract the TV fulfils. The 2026-07-26 experiments with
            // nonzero figures here and/or in RECORD proved senders don't
            // honor those fields consistently (realtime video ignored this
            // array; two nonzero surfaces were counted cumulatively) — sync
            // must come from anchor-exact rendering, not declarations.
            { "audioLatencies", BuildAudioLatencies() },
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

        // A sender sizes and paces its video encoder from this array, and
        // without it a Mac completes the whole session — pairing, fp-setup,
        // session SETUP, RECORD — and then tears down without ever asking for a
        // video stream, because as far as it knows there is no screen here to
        // send one to. Advertised only when mirroring is enabled: the audio
        // negotiation is proven against an /info that has no displays key.
        if (ReceiverFeatures.ScreenMirroringAdvertised)
        {
            info.Add("displays", new NSArray(BuildDisplay(display, identity.Pi)));
        }

        return PlistReply(info);
    }

    /// <summary>
    /// The screen we claim to be. The uuid is derived from our stable identity
    /// rather than generated per session, so a sender that remembers this
    /// display across reconnects sees the same one.
    /// </summary>
    internal static NSDictionary BuildDisplay(MirrorDisplay display, string uuid) => new()
    {
        { "features", new NSNumber(14) },
        { "height", new NSNumber(display.Height) },
        { "heightPhysical", new NSNumber(0) },
        { "heightPixels", new NSNumber(display.Height) },
        { "maxFPS", new NSNumber(display.Fps) },
        { "overscanned", new NSNumber(false) },
        { "refreshRate", new NSNumber(display.Fps) },
        { "rotation", new NSNumber(false) },
        { "uuid", new NSString(uuid) },
        { "width", new NSNumber(display.Width) },
        { "widthPhysical", new NSNumber(0) },
        { "widthPixels", new NSNumber(display.Width) },
    };

    /// <summary>
    /// The TV's exact entry set (types 100/101/102, same audioType variants,
    /// same key order, all-zero latencies).
    /// </summary>
    private static NSArray BuildAudioLatencies() => new(
        AudioLatency(100, null),
        AudioLatency(100, "default"),
        AudioLatency(100, "media"),
        AudioLatency(100, "telephony"),
        AudioLatency(100, "speechRecognition"),
        AudioLatency(100, "alert"),
        AudioLatency(101, null),
        AudioLatency(101, "default"),
        AudioLatency(102, "media"));

    private static NSDictionary AudioLatency(int type, string? audioType)
    {
        var dict = new NSDictionary { { "inputLatencyMicros", new NSNumber(0) } };
        if (audioType is not null)
        {
            dict.Add("audioType", new NSString(audioType));
        }

        dict.Add("type", new NSNumber(type));
        dict.Add("outputLatencyMicros", new NSNumber(0));
        return dict;
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
        byte[] body;
        switch (request.Body.Length)
        {
            case 16:
                body = FairPlaySetup.HandleSetupPhase1(request.Body);
                break;
            case 164:
                // Retain it: a later stream SETUP may key itself with an ekey,
                // which is meaningless without this exact message.
                _fairPlayKeyMessage = request.Body;
                body = FairPlaySetup.HandleSetupPhase2(request.Body);
                break;
            default:
                throw new IOException($"unexpected fp-setup request length {request.Body.Length}");
        }

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

        // Mirror video stamps are read against whichever of these the sender
        // chose, so capture it before any stream SETUP arrives.
        _senderUsesPtpTiming = tp?.ToString().Contains("PTP", StringComparison.OrdinalIgnoreCase) ?? false;
        _senderTimingPort = plist.TryGetValue("timingPort", out var senderTimingPort) && senderTimingPort is NSNumber port
            ? (int)port.ToLong()
            : 0;
        if (plist.TryGetValue("ekey", out var sessionEkey) && sessionEkey is NSData ekeyData)
        {
            _sessionEncryptedKey = ekeyData.Bytes;
            logger.LogInformation("session SETUP carries a FairPlay ekey ({Bytes} B)", _sessionEncryptedKey.Length);
        }

        if (plist.TryGetValue("eiv", out var sessionEiv) && sessionEiv is NSData eivData)
        {
            _sessionEncryptedIv = eivData.Bytes;
        }
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
        //
        // A screen-mirroring session is NTP-timed and never speaks PTP, so
        // starting the grandmaster for one only adds a peer that is tracked and
        // dropped again with nothing in between. Start our own timing client
        // against the sender instead — and start it HERE rather than at stream
        // SETUP, so the offset has locked by the time the first video packet
        // arrives; a frame whose stamp cannot be translated yet goes out
        // unstamped and loses its place on the group timeline.
        _senderIsMirroring = IsMirroringSession(plist);
        if (_senderIsMirroring)
        {
            StartMirrorTiming();
        }
        else if (IsOwnAddress(CleanAddress(connection.RemoteAddress)))
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
            // Dummy under PTP, but a mirroring sender is told the real port our
            // timing client speaks from.
            { "timingPort", new NSNumber(_mirrorTiming?.LocalPort ?? 0) },
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
            case 110:
                return SetupMirrorStream(stream);
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
        _aacPcmMapper = null;
        _decoder = new AacDecoderPipe(logger);
        _emitter = new PacedPcmEmitter(
            _decoder.Pcm,
            (pcm, target) => source.PushDecodedPcm(pcm, target),
            presentationLatencySamples * 1_000_000_000L / 44100);
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
    /// from the protocol's fixed parameters. No SETRATEANCHORTIME exists in
    /// this mode; render timing comes from the control channel's 0xD7
    /// anchors instead, via <see cref="AnchoredPcmScheduler"/> — the sender
    /// transmits well ahead of presentation, so arrival time is meaningless.
    /// </summary>
    private RtspReply SetupRealtimeStream(NSDictionary stream)
    {
        // Full dump — ground truth for the modern (AirPlay 3.x sdk) stream
        // negotiation: streamConnections/streamConnectionID arrived here long
        // before any reference receiver code handled them.
        logger.LogDebug("realtime stream SETUP: {Stream}", stream.ToXmlPropertyList());

        // A screen mirror's companion audio arrives here and names no key: it
        // sets streamConnectionKeyUseStreamEncryptionKey and expects the key to
        // come from the pairing, exactly as its video stream does.
        // A mirror's companion audio names no key either, and sets the same
        // streamConnectionKeyUseStreamEncryptionKey flag its video does — so
        // try that stream's own data-stream derivation FIRST, then the older
        // shapes. The Poly1305 tag picks the winner.
        var audioKeys = StreamData(stream, "shk") is { Length: > 0 } shk
            ? StreamKeyCandidates.Single(shk)
            : [
                .. StreamKeyCandidates.ForDataStream(_keys?.SharedSecret ?? [], StreamConnectionId(stream)),
                .. StreamKeyCandidates.FromSharedSecret(_keys?.SharedSecret ?? []),
            ];
        if (audioKeys.Count == 0)
        {
            logger.LogWarning("realtime stream SETUP named no key and the session established none");
            return RtspReply.Error(400, "Bad Request");
        }

        var streamId = StreamId(stream);

        // ct 2 = ALAC (what macOS system output sends), ct 4 = AAC-LC (what a
        // screen mirror's companion audio sends, at 1024 samples per frame).
        var compression = stream.TryGetValue("ct", out var ctObj) ? (int)((NSNumber)ctObj).ToLong() : 2;
        var spf = stream.TryGetValue("spf", out var s) ? (int)((NSNumber)s).ToLong() : PcmFrame.SamplesPerFrame;
        if (compression == 4)
        {
            return SetupRealtimeAacStream(stream, streamId, audioKeys, spf, MirrorAudioCipherOrNull());
        }

        var alac = new AlacDecoder(AlacSpecificConfig(spf));
        _streamCts = new CancellationTokenSource();
        _aacPcmMapper = null;

        // Decoded frames carry their 0xD7-anchored render deadline, not
        // arrival time: the modern sender transmits ~1.75 s ahead of
        // presentation with a per-session-variable lead, so arrival is the
        // wrong clock for lip sync. The sink derives its send timeline from
        // the stamps. MarkActive rides each emit (no-op while Active): the
        // slot is claimed when audio actually flows, and re-claimed after a
        // FLUSH left the source Paused.
        var scheduler = new AnchoredPcmScheduler((pcm, target) =>
        {
            source.MarkActive();
            source.PushDecodedPcm(pcm, target);
        }, logger, presentationLatencySamples * 1_000_000_000L / 44100);
        _scheduler = scheduler;
        source.SetRealtimeScheduler(scheduler);
        _ = RunSchedulerAsync(scheduler, _streamCts.Token);

        var pcmBuffer = new byte[alac.MaxBytesPerPacket];
        var pending = new List<byte>(PcmFrame.CanonicalFrameBytes * 2);
        var active = false;
        var nextRtp = 0u;
        _realtimeServer = new RealtimeAudioServer(audioKeys, logger)
        {
            OnAnchor = scheduler.SetAnchor,
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
                    logger.LogInformation("realtime audio flowing (seq {Seq}, {Bytes} B PCM/packet)",
                        packet.Sequence, written);
                }

                // An empty carry list means the next sample out is this
                // packet's first sample — re-sync the running RTP cursor.
                if (pending.Count == 0)
                {
                    nextRtp = packet.RtpTime;
                }

                // Full ALAC frames are exactly one canonical frame; the carry
                // list only ever holds a trailing partial.
                if (pending.Count == 0 && written == PcmFrame.CanonicalFrameBytes)
                {
                    scheduler.Enqueue(packet.RtpTime, pcmBuffer.AsSpan(0, written).ToArray());
                    return ValueTask.CompletedTask;
                }

                pending.AddRange(pcmBuffer.AsSpan(0, written).ToArray());
                var offset = 0;
                while (pending.Count - offset >= PcmFrame.CanonicalFrameBytes)
                {
                    var frame = new byte[PcmFrame.CanonicalFrameBytes];
                    pending.CopyTo(offset, frame, 0, frame.Length);
                    offset += frame.Length;
                    scheduler.Enqueue(nextRtp, frame);
                    nextRtp += (uint)PcmFrame.SamplesPerFrame;
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
    /// The companion audio of a screen mirror: realtime transport like the ALAC
    /// path above, but carrying AAC-LC at 1024 samples per frame rather than
    /// ALAC at 352. It shares the ChaCha envelope, the 0xD7 anchors and the
    /// scheduler; only the decoder differs.
    ///
    /// ffmpeg returns unframed PCM, so keep every accepted packet's RTP time
    /// beside its decoded 1024-sample block. Counting only decoded samples
    /// would erase lost packets from time and gradually exhaust output lead.
    /// </summary>
    /// <summary>
    /// The CBC cipher a screen mirror's companion audio needs, or null for an
    /// ordinary music session. A mirror's audio is keyed from exactly the same
    /// material as its video — SHA-512(aesKey ‖ ecdhSecret)[:16] — with the
    /// session SETUP's eiv as the IV, and carries no authentication tag.
    /// </summary>
    private MirrorAudioCipher? MirrorAudioCipherOrNull()
    {
        if (_keys?.SharedSecret is not { Length: > 0 } secret || !_senderIsMirroring)
        {
            return null;
        }

        ReadOnlySpan<byte> aesKey = default;
        if (_sessionEncryptedKey is { Length: > 0 } ekey && _fairPlayKeyMessage is { } keyMessage)
        {
            aesKey = FairPlayKeys.UnwrapStreamKey(keyMessage, ekey);
        }

        var material = MirrorAesCtrCipher.DeriveKeyMaterial(aesKey, secret);
        logger.LogInformation(
            "mirror companion audio: AES-CBC keyed from the session material ({Source})",
            aesKey.Length > 0 ? "FairPlay ekey + pairing secret" : "pairing secret only");
        return new MirrorAudioCipher(material, _sessionEncryptedIv ?? new byte[16]);
    }

    private RtspReply SetupRealtimeAacStream(
        NSDictionary stream, long streamId, IReadOnlyList<StreamKeyCandidate> audioKeys, int spf,
        MirrorAudioCipher? mirrorCipher)
    {
        _streamCts = new CancellationTokenSource();
        var decoder = new AacDecoderPipe(logger);
        _decoder = decoder;

        var scheduler = new AnchoredPcmScheduler((pcm, target) =>
        {
            source.MarkActive();
            source.PushDecodedPcm(pcm, target);
        }, logger, presentationLatencySamples * 1_000_000_000L / 44100);
        _scheduler = scheduler;
        source.SetRealtimeScheduler(scheduler);
        _ = RunSchedulerAsync(scheduler, _streamCts.Token);

        var mapper = new AacRtpPcmMapper(scheduler.Enqueue, scheduler.Flush);
        _aacPcmMapper = mapper;
        _ = RunAacDecodeLoopAsync(decoder, mapper, _streamCts.Token);

        var active = false;
        Func<RealtimeAudioPacket, CancellationToken, ValueTask> onPacket = async (packet, ct) =>
            {
                // Register before writing: decoder stdout may become readable
                // before WriteFrameAsync finishes flushing its input pipe.
                if (!mapper.TryQueuePacket(packet.RtpTime))
                {
                    return;
                }

                if (!active)
                {
                    active = true;
                    logger.LogInformation(
                        "mirror companion audio flowing (AAC-LC, seq {Seq}, {Spf} samples/frame)", packet.Sequence, spf);
                }

                await decoder.WriteFrameAsync(packet.Frame, ct).ConfigureAwait(false);
            };

        // Verifiable keys first, CBC only as a last resort — ChaCha's tag can
        // prove itself right, CBC can only fail silently.
        _realtimeServer = new RealtimeAudioServer(audioKeys, logger)
        {
            OnAnchor = scheduler.SetAnchor,
            OnPacket = onPacket,
            LegacyCbcFallback = mirrorCipher,
        };
        _realtimeServer.Start();

        logger.LogInformation("realtime AAC stream SETUP ok (data :{Data}, control :{Control})",
            _realtimeServer.DataPort, _realtimeServer.ControlPort);
        return PlistReply(new NSDictionary
        {
            { "streams", new NSArray(BuildRealtimeReply(stream, streamId, 96)) },
        });
    }

    /// <summary>
    /// Matches arbitrary decoder stdout chunks to the original AAC RTP times.
    /// </summary>
    private async Task RunAacDecodeLoopAsync(
        AacDecoderPipe decoder, AacRtpPcmMapper mapper, CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in decoder.Pcm.ReadAllAsync(ct).ConfigureAwait(false))
            {
                mapper.WriteDecodedPcm(chunk);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AAC decode loop failed");
        }
    }

    /// <summary>
    /// The realtime stream reply both codec paths share: ports, the echoed
    /// connection id, and the streamConnections mirror the modern transport
    /// reads its ports from.
    /// </summary>
    private NSDictionary BuildRealtimeReply(NSDictionary stream, long streamId, int type)
    {
        var replyStream = new NSDictionary
        {
            { "streamID", new NSNumber(streamId) },
            { "type", new NSNumber(type) },
            { "dataPort", new NSNumber(_realtimeServer!.DataPort) },
            { "controlPort", new NSNumber(_realtimeServer.ControlPort) },
        };
        if (stream.TryGetValue("streamConnectionID", out var scid))
        {
            replyStream.Add("streamConnectionID", scid);
        }

        if (stream.ContainsKey("streamConnections"))
        {
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

        return replyStream;
    }

    /// <summary>
    /// Screen mirroring (type 110): H.264 access units over their own TCP data
    /// channel, keyed per stream and stamped on the sender's timing clock. The
    /// shape is the video analogue of the realtime audio stream — we open a
    /// listener, hand its port back as <c>dataPort</c>, and the sender connects
    /// in — but the failure modes differ enough to be worth naming. There is no
    /// loss and no reordering to tolerate (it is TCP), and there is no format
    /// negotiation: the sender states its parameter sets in a config packet
    /// once the channel is up, and we pass its H.264 through untouched.
    /// </summary>
    private RtspReply SetupMirrorStream(NSDictionary stream)
    {
        logger.LogInformation("mirror stream SETUP: {Stream}", stream.ToXmlPropertyList());

        var connectionId = StreamConnectionId(stream);
        var request = new MirrorKeyRequest(
            SharedKey: StreamData(stream, "shk"),
            EncryptedKey: StreamData(stream, "ekey") ?? _sessionEncryptedKey,
            FairPlayKeyMessage: _fairPlayKeyMessage,
            StreamConnectionId: connectionId,
            SessionKey: _keys?.SharedSecret);

        IMirrorStreamCipher cipher;
        MirrorKeySource keySource;
        try
        {
            cipher = request.Resolve(out keySource);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            // Refuse rather than accept and mis-key: a sender told no gives up
            // cleanly, while one whose key we got wrong streams megabytes of
            // undecodable video and reports nothing wrong at either end.
            logger.LogWarning(ex, "mirror stream SETUP carried unusable key material");
            return RtspReply.Error(400, "Bad Request");
        }

        var mapTimestamp = BuildMirrorTimestampMapping();

        _mirrorServer = new MirrorDataServer(cipher, logger)
        {
            // The modern envelope is tried first: a HomeKit-paired sender keys
            // its video with HKDF+ChaCha20-Poly1305, and only a legacy sender
            // uses the AES-CTR scheme above. The Poly1305 tag decides, so this
            // costs one packet and cannot pick wrong.
            DataStreamCandidates = _keys?.SharedSecret is { Length: > 0 } dsSecret
                ? MirrorDataStreamCipher.Candidates(dsSecret, connectionId)
                : null,
            MapTimestamp = mapTimestamp,
            OnCodecConfig = videoSource.PushCodecConfig,
            OnAccessUnit = async (frame, ct) =>
            {
                videoSource.MarkActive();
                await videoSource.PushAccessUnitAsync(frame, ct).ConfigureAwait(false);
            },
            OnClosed = videoSource.MarkIdle,
        };
        _mirrorServer.Start();

        logger.LogInformation(
            "mirror stream SETUP ok (data :{Data}, key {Key}, streamConnectionID {Id})",
            _mirrorServer.Port, keySource.Describe(), connectionId);

        var replyStream = new NSDictionary
        {
            { "streamID", new NSNumber(StreamId(stream)) },
            { "type", new NSNumber(110) },
            { "dataPort", new NSNumber(_mirrorServer.Port) },
        };
        if (stream.TryGetValue("streamConnectionID", out var scid))
        {
            replyStream.Add("streamConnectionID", scid);
        }

        return PlistReply(new NSDictionary { { "streams", new NSArray(replyStream) } });
    }

    /// <summary>
    /// A session the sender opened to mirror its screen. macOS says so
    /// outright; anything else declaring NTP timing with a port to poll is
    /// treated the same way, since that combination only arises for video.
    /// </summary>
    private bool IsMirroringSession(NSDictionary plist) =>
        (plist.TryGetValue("isScreenMirroringSession", out var flag) && flag is NSNumber { } n && n.ToBool())
        || (!_senderUsesPtpTiming && _senderTimingPort > 0);

    /// <summary>Starts polling the sender's clock, if it gave us somewhere to poll.</summary>
    private void StartMirrorTiming()
    {
        if (_mirrorTiming is not null || _senderTimingPort <= 0)
        {
            return;
        }

        var peer = new IPEndPoint(CleanAddress(connection.RemoteAddress), _senderTimingPort);
        _mirrorTiming = new MirrorNtpClient(peer, logger);
        _mirrorTiming.Start();
        logger.LogInformation("mirror timing: polling the sender's NTP clock at {Peer}", peer);
    }

    /// <summary>
    /// Turns a mirror packet's presentation stamp into grandmaster nanoseconds.
    /// Under PTP the sender has already disciplined to our clock, so the stamp
    /// needs nothing but a format conversion; under NTP it is on a clock we
    /// have to go and measure, and frames that arrive before that measurement
    /// lands are emitted unstamped rather than scheduled against a guess.
    /// </summary>
    private Func<ulong, long> BuildMirrorTimestampMapping()
    {
        StartMirrorTiming();
        if (_mirrorTiming is not { } timing)
        {
            logger.LogInformation(
                "mirror stamps read directly on the hub grandmaster (sender offered no NTP clock to poll)");
            return MirrorPacketHeader.PresentationStampToNanos;
        }

        return ntp => timing.IsLocked ? timing.ToLocalNanos(MirrorPacketHeader.PresentationStampToNanos(ntp)) : 0;
    }

    private static byte[]? StreamData(NSDictionary stream, string key) =>
        stream.TryGetValue(key, out var value) && value is NSData data ? data.Bytes : null;

    /// <summary>
    /// The SETUP's <c>streamConnectionID</c>. It is hashed into the stream key
    /// and IV, so it is not bookkeeping — a missing or misread value produces a
    /// cipher that decrypts everything to noise. Senders send it as a number;
    /// some send the decimal string, which is the form it is hashed in anyway.
    /// </summary>
    private static ulong StreamConnectionId(NSDictionary stream)
    {
        if (!stream.TryGetValue("streamConnectionID", out var value))
        {
            return 0;
        }

        return value switch
        {
            NSNumber number => unchecked((ulong)number.ToLong()),
            NSString text when ulong.TryParse(text.Content, out var parsed) => parsed,
            _ => 0,
        };
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

    private async Task RunSchedulerAsync(AnchoredPcmScheduler scheduler, CancellationToken ct)
    {
        try
        {
            await scheduler.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "anchored PCM scheduler failed");
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
        // 0 is the truth (see the audioLatencies note in InfoReply): anchored
        // render times are honored by emitting one group latency early, so
        // audio is audible AT the sender's stated time with nothing left to
        // declare.
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
        if (rate >= 1)
        {
            // "rtpTime renders at networkTimeSecs+Frac" — on OUR timeline,
            // since the hub clock is the grandmaster inbound senders sync to.
            // The sender holds its local video back to exactly this instant,
            // so the anchor becomes every frame's render stamp: the emitter
            // stamps frames anchor+offset, and the sink derives its send
            // timeline from the stamps — presentation lands on the anchor
            // with no estimated pipeline constants in between.
            if (AnchorNanos(plist) is { } anchorNanos)
            {
                var leadMs = (anchorNanos - PtpReceiverClock.NowNanos) / 1e6;
                if (Math.Abs(leadMs) > 5000)
                {
                    logger.LogWarning(
                        "SETRATEANCHORTIME anchor {Lead:F0} ms from now — honoring, but a lead this size means clock trouble",
                        leadMs);
                }

                logger.LogInformation(
                    "SETRATEANCHORTIME rate={Rate} — frames stamped audible at anchor ({Lead:F0} ms ahead)",
                    rate, leadMs);
                _emitter?.GoAt(anchorNanos);
            }
            else
            {
                logger.LogInformation("SETRATEANCHORTIME rate={Rate} — no network time in body; gate opens now", rate);
                _emitter?.Go();
            }

            source.MarkActive();
        }
        else
        {
            logger.LogInformation("SETRATEANCHORTIME rate={Rate}", rate);
        }

        return RtspReply.Ok();
    }

    /// <summary>
    /// The anchor's network time as grandmaster nanoseconds: whole seconds
    /// plus a 2^64 fraction (the sender-side frac64 math in reverse). Null
    /// when the body carries no network time (a bare rate change).
    /// </summary>
    internal static long? AnchorNanos(NSDictionary plist)
    {
        if (!plist.TryGetValue("networkTimeSecs", out var secsObj) || secsObj is not NSNumber secs)
        {
            return null;
        }

        var frac64 = plist.TryGetValue("networkTimeFrac", out var fracObj) && fracObj is NSNumber frac
            ? unchecked((ulong)frac.ToLong())
            : 0UL;
        var fracNanos = (long)(((UInt128)frac64 * 1_000_000_000) >> 64);
        return secs.ToLong() * 1_000_000_000L + fracNanos;
    }

    private RtspReply SetParameterReply(RtspRequest request)
    {
        var contentType = request.Header("Content-Type") ?? "";
        if (contentType.Contains("dmap", StringComparison.OrdinalIgnoreCase))
        {
            if (DmapMetadata.Parse(request.Body) is { } parsed)
            {
                var trackId = parsed.PersistentId is { } id ? $"airplay:{id:x16}" : null;
                var textChanged = (parsed.Title is not null && parsed.Title != _metadata.Title)
                    || (parsed.Artist is not null && parsed.Artist != _metadata.Artist)
                    || (parsed.Album is not null && parsed.Album != _metadata.Album);
                var trackChanged = trackId is not null ? trackId != _metadata.TrackId : textChanged;

                // The listing and cover arrive separately. Start a new track
                // without the previous cover, but merge optional fields on
                // same-track updates (including listings that omit mper).
                var previous = trackChanged ? new TrackMetadata() : _metadata;
                var next = previous with
                {
                    Title = parsed.Title ?? previous.Title,
                    Artist = parsed.Artist ?? previous.Artist,
                    Album = parsed.Album ?? previous.Album,
                    TrackId = trackId ?? previous.TrackId,
                };

                _metadata = next;
                source.PushMetadata(_metadata);
                logger.LogInformation("now playing: {Artist} — {Title}", parsed.Artist, parsed.Title);
            }
        }
        else if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            _metadata = _metadata with
            {
                Artwork = request.Body.Length > 0 ? request.Body : null,
                ArtworkMimeType = request.Body.Length > 0 ? contentType : null,
            };
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
        if (_aacPcmMapper is { } mapper)
        {
            // Preserve decoder byte/packet correspondence while discarding
            // pre-flush generations, including a partially returned AAC block.
            mapper.Flush();
        }
        else
        {
            // Buffered AAC has no per-packet RTP mapping to keep aligned.
            while (_decoder?.Pcm.TryRead(out _) == true)
            {
            }

            _scheduler?.Flush();
        }

        // Pause must reach the speakers too: Paused makes the pump flush the
        // fan-out, silencing the group-latency's worth of audio already in
        // flight (audibly: pause used to keep playing for the whole group
        // latency). The re-anchor that follows marks Active again.
        source.MarkPaused();
        logger.LogInformation("FLUSH — queued audio dropped, source paused until re-anchor");
        return RtspReply.Ok();
    }

    private RtspReply TeardownReply(RtspRequest request)
    {
        var streams = request.Body.Length > 0
            && PropertyListParser.Parse(request.Body) is NSDictionary plist
            && plist.TryGetValue("streams", out var streamsObj)
            && streamsObj is NSArray array
                ? array.OfType<NSDictionary>().ToList()
                : null;

        if (streams is null)
        {
            logger.LogInformation("TEARDOWN (session)");
            StopStream();
            return RtspReply.Ok();
        }

        // A stream-scoped teardown is ROUTINE, not an ending: a sender that
        // advertised supportsDynamicStreamID replaces its audio stream this
        // way (teardown one type 96, SETUP the next) while the mirror video
        // keeps flowing. Tearing the whole session down here took the video
        // with it, marked both sources idle, and ended the mirror over an
        // audio format switch.
        var types = streams
            .Select(s => s.TryGetValue("type", out var t) && t is NSNumber n ? n.ToLong() : -1)
            .ToList();
        logger.LogInformation("TEARDOWN (stream: {Types}) — other streams keep flowing",
            string.Join(", ", types));

        foreach (var type in types)
        {
            switch (type)
            {
                case 96 or 103:
                    StopAudioStream();
                    break;
                case 110:
                    StopVideoStream();
                    break;
                default:
                    logger.LogWarning("TEARDOWN named unrecognized stream type {Type} — nothing stopped for it", type);
                    break;
            }
        }

        return RtspReply.Ok();
    }

    private void StopAudioStream()
    {
        _streamCts?.Cancel();
        _audioServer?.Dispose();
        _realtimeServer?.Dispose();
        _decoder?.Dispose();
        _controlSocket?.Dispose();
        _audioServer = null;
        _realtimeServer = null;
        _decoder = null;
        _aacPcmMapper = null;
        _emitter = null;
        _scheduler = null;
        source.SetRealtimeScheduler(null);
        _controlSocket = null;
        source.MarkIdle();
    }

    private void StopVideoStream()
    {
        _mirrorServer?.Dispose();
        _mirrorServer = null;
        videoSource.MarkIdle();
    }

    private void StopStream()
    {
        StopAudioStream();
        StopVideoStream();
        // Session-scoped: the timing client outlives any one stream — a
        // replacement video stream reuses the lock instead of re-acquiring it.
        _mirrorTiming?.Dispose();
        _mirrorTiming = null;
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

/// <summary>Minimal DMAP (DAAP) parser for inbound track identity and now-playing text.</summary>
internal static class DmapMetadata
{
    public sealed record Parsed(string? Title, string? Artist, string? Album, ulong? PersistentId);

    public static Parsed? Parse(byte[] body)
    {
        string? title = null, artist = null, album = null;
        ulong? persistentId = null;
        Walk(body, 0, body.Length);
        return title is null && artist is null && album is null && persistentId is null
            ? null : new Parsed(title, artist, album, persistentId);

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
                    case "mper" when length == 8:
                        persistentId = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(valueStart, length));
                        break;
                }

                offset = valueStart + length;
            }
        }
    }
}
