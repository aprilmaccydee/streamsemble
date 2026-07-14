using System.Net;
using System.Security.Cryptography;
using Claunia.PropertyList;
using Microsoft.Extensions.Logging;
using Streamsemble.AirPlay.Common;
using Streamsemble.AirPlay.Common.Hap;
using Streamsemble.AirPlay.Sender.Raop;
using Streamsemble.Core.Metadata;

namespace Streamsemble.AirPlay.Sender.AirPlay2;

/// <summary>
/// One session with a HAP-paired AirPlay 2 speaker (HomePod). Runs transient
/// pair-setup, upgrades the RTSP connection to the encrypted control channel,
/// negotiates a realtime audio stream, and exposes the per-packet audio cipher
/// for the fan-out to send RTP.
///
/// Built to the pair_ap / OwnTone spec (transient SRP → 64-byte secret; control
/// keys via HKDF, audio key = first 32 bytes used directly; realtime stream
/// type 96). The pairing/crypto path is unit-tested; the SETUP plist field set
/// is the piece that needs tuning against real HomePod firmware.
/// </summary>
public sealed class AirPlay2Session(string displayName, IPAddress address, int rtspPort, int latencyTrimMs, ILogger logger, string streamMode = "Auto") : ITargetSession
{
    private readonly RtspClient _rtsp = new(logger);
    // The URI's session id must match the sessionUUID sent in SETUP (uppercase).
    private readonly string _sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
    private AirPlay2AudioCipher? _audioCipher;
    private uint _lastKnownRtpTime;

    public string DisplayName { get; } = displayName;
    public int LatencyTrimMs { get; } = latencyTrimMs;
    public IPAddress DeviceAddress { get; } = address;
    public bool RequiresPtp => true;

    /// <summary>
    /// Buffered (type 103, AAC over TCP) vs realtime (type 96, ALAC over UDP).
    /// Decided during connect: config override, else /info audioLatencies —
    /// receivers that don't list type 96 (TVs) only render buffered streams.
    /// </summary>
    public bool IsBuffered { get; private set; }

    /// <summary>
    /// All timing-group member addresses (this receiver + the sender + any other
    /// receivers), set by the group before connect. Sent in SETPEERS so a
    /// multi-room group elects one shared PTP grandmaster.
    /// </summary>
    public IReadOnlyList<string> GroupPeerAddresses { get; set; } = [];

    /// <summary>Receiver-reported playback latency (RECORD Audio-Latency header, samples at 44.1k); 0 if not reported.</summary>
    public int ReportedAudioLatencySamples { get; private set; }

    public IPEndPoint AudioEndpoint { get; private set; } = new(address, 0);
    public IPEndPoint ControlEndpoint { get; private set; } = new(address, 0);
    public AirPlay2AudioCipher AudioCipher => _audioCipher ?? throw new InvalidOperationException("Session not set up");

    /// <summary>RTP header (12 bytes) ‖ ChaCha20-encrypted ALAC-verbatim payload ‖ tag ‖ counter (ct=2 realtime ALAC).</summary>
    public byte[] PrepareWirePacket(byte[] rtpHeader, byte[] pcm, ushort sequenceNumber, uint rtpTimestamp)
    {
        var alac = new byte[Raop.AlacPacker.PackedLength(pcm.Length / 4)];
        var alacLength = Raop.AlacPacker.Pack(pcm, alac);
        var encrypted = AudioCipher.Encrypt(sequenceNumber, rtpHeader, alac.AsSpan(0, alacLength));
        var packet = new byte[12 + encrypted.Length];
        rtpHeader.CopyTo(packet, 0);
        encrypted.CopyTo(packet.AsSpan(12));
        return packet;
    }

    public void NoteRtpTime(uint rtpTime) => _lastKnownRtpTime = rtpTime;

    public async Task SetVolumeAsync(float linear, CancellationToken ct)
    {
        var db = linear <= 0.001f ? -144.0 : -30.0 + linear * 30.0;
        var body = System.Text.Encoding.ASCII.GetBytes($"volume: {db:F6}\r\n");
        await _rtsp.RequestAsync("SET_PARAMETER", ct, "text/parameters", body).ConfigureAwait(false);
    }

    public Task SetMetadataAsync(TrackMetadata metadata, CancellationToken ct) => Task.CompletedTask;

    public async Task FlushAsync(ushort nextSeq, uint nextRtpTime, CancellationToken ct)
    {
        if (IsBuffered)
        {
            // Buffered streams use FLUSHBUFFERED, and only to discard queued
            // audio mid-session; a fresh stream has nothing to flush.
            return;
        }

        await _rtsp.RequestAsync("FLUSH", ct, extraHeaders: new Dictionary<string, string>
        {
            ["RTP-Info"] = $"seq={nextSeq};rtptime={nextRtpTime}",
        }).ConfigureAwait(false);
    }

    public async Task ConnectAsync(int ourTimingPort, int ourControlPort, ushort startSeq, uint startRtpTime, CancellationToken ct)
    {
        await _rtsp.ConnectAsync(address, rtspPort, ct).ConfigureAwait(false);
        _rtsp.Uri = $"rtsp://{_rtsp.LocalAddress}/{_sessionUuid}";
        _rtsp.DefaultHeaders["User-Agent"] = "AirPlay/665.13";
        _rtsp.DefaultHeaders["Client-Instance"] = RandomHex(16);
        _rtsp.DefaultHeaders["DACP-ID"] = RandomHex(16);
        _rtsp.DefaultHeaders["Active-Remote"] = ((uint)Random.Shared.Next()).ToString();

        var sharedSecret = await PairAsync(ct).ConfigureAwait(false);
        var keys = HapSessionKeys.Derive(sharedSecret, controllerRole: true);
        _keys = keys;
        _audioCipher = new AirPlay2AudioCipher(keys.AudioKey);
        _audioKeyForWire = keys.AudioKey;

        await _rtsp.UpgradeStreamAsync(inner => new HapCipherStream(inner, keys.ControlReadKey, keys.ControlWriteKey), ct)
            .ConfigureAwait(false);
        logger.LogInformation("{Name}: HAP pairing complete ({Mode}); control channel encrypted", DisplayName, _pairingMode);

        var latencyTypes = await LogReceiverInfoAsync(ct).ConfigureAwait(false);
        _bufferedAlac = streamMode.Equals("BufferedAlac", StringComparison.OrdinalIgnoreCase);
        IsBuffered = _bufferedAlac
            || streamMode.Equals("Buffered", StringComparison.OrdinalIgnoreCase)
            || (streamMode.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                && latencyTypes.Count > 0 && !latencyTypes.Contains(96));
        logger.LogInformation(
            "{Name}: stream mode {Mode} (configured {Configured}; receiver latency types: {Types})",
            DisplayName, IsBuffered ? "Buffered/AAC" : "Realtime/ALAC", streamMode,
            latencyTypes.Count == 0 ? "none advertised" : string.Join(",", latencyTypes));

        await SetupAsync(ourTimingPort, ourControlPort, startSeq, startRtpTime, ct).ConfigureAwait(false);
    }

    // Real AirPlay 2 devices route pairing by the X-Apple-HKP header:
    // 4 = HomeKit transient pairing, 6 = verified HomeKit pairing (setup+verify).
    // Without it they reject with 403.
    private static Dictionary<string, string> PairHeaders(int hkp) => new()
    {
        ["X-Apple-HKP"] = hkp.ToString(),
        ["Connection"] = "keep-alive",
    };

    private static readonly Dictionary<string, string> TransientPairHeaders = PairHeaders(4);

    private string _pairingMode = "transient";
    // Verified HomeKit pairing (pair-setup + pair-verify) uses X-Apple-HKP: 3,
    // per pyatv's proven AirPlay 2 pairing. (4 = transient; 6 is a different
    // handler that rejects our SRP proof with Authentication.)
    private static int VerifiedHkp =>
        int.TryParse(Environment.GetEnvironmentVariable("STREAMSEMBLE_HKP"), out var v) ? v : 3;

    /// <summary>
    /// Choose the pairing path: verified pair-verify if we already paired with
    /// this device; PIN-based pair-setup (then verify) when a PIN is supplied
    /// via <c>STREAMSEMBLE_PAIR_PIN</c>; otherwise transient (HomePods/Sonos,
    /// which render without a persistent identity). Verified receivers (TVs)
    /// only render buffered audio over a pair-verify session.
    /// </summary>
    private async Task<byte[]> PairAsync(CancellationToken ct)
    {
        var deviceKey = address.ToString();
        var store = HomeKitPairingStore.Load();
        var pin = Environment.GetEnvironmentVariable("STREAMSEMBLE_PAIR_PIN");
        var wantPair = Environment.GetEnvironmentVariable("STREAMSEMBLE_PAIR") == "1"
            || !string.IsNullOrWhiteSpace(pin);

        if (store.GetAccessory(deviceKey) is null && wantPair)
        {
            await PairSetupAsync(store, deviceKey, pin, ct).ConfigureAwait(false);
        }

        if (store.GetAccessory(deviceKey) is { } accessory)
        {
            _pairingMode = "verified";
            return await PairVerifyAsync(store, accessory, ct).ConfigureAwait(false);
        }

        _pairingMode = "transient";
        return await TransientPairAsync(ct).ConfigureAwait(false);
    }

    private async Task PairSetupAsync(HomeKitPairingStore store, string deviceKey, string? pin, CancellationToken ct)
    {
        logger.LogInformation("{Name}: starting PIN pair-setup (persisting identity for future connects)", DisplayName);

        // /pair-pin-start MUST run on this same RTSP connection immediately
        // before pair-setup: HomeKit binds the displayed PIN to the connection's
        // pairing session, and calling it rolls the code to a fresh value. So we
        // trigger it here, then wait for the operator to read the just-displayed
        // PIN and drop it in the pin file (env PIN can't match a code we just
        // rolled on our own connection).
        await _rtsp.RequestAsync("POST", ct, "application/octet-stream", [], PairHeaders(VerifiedHkp), "/pair-pin-start").ConfigureAwait(false);

        // Send M1 and capture the salt/B immediately, while the pairing session
        // is fresh — THEN wait for the operator to read the PIN. Waiting before
        // M1 lets the accessory roll its verifier and rejects our proof.
        var client = new PairSetupClient(store);
        var m2 = await PostPairAsync("/pair-setup", client.BuildM1(), VerifiedHkp, ct).ConfigureAwait(false);
        client.HandleM2(m2);

        pin = await WaitForPinAsync(pin, ct).ConfigureAwait(false);
        var m4 = await PostPairAsync("/pair-setup", client.BuildM3(pin), VerifiedHkp, ct).ConfigureAwait(false);
        var m5 = client.HandleM4BuildM5(m4);
        var m6 = await PostPairAsync("/pair-setup", m5, VerifiedHkp, ct).ConfigureAwait(false);
        client.HandleM6(m6);

        store.SaveAccessory(deviceKey, client.AccessoryId, client.AccessoryLtpk);
        logger.LogInformation("{Name}: PIN pair-setup complete — paired identity stored", DisplayName);
    }

    /// <summary>
    /// Wait for the freshly-displayed PIN. Polls the pin file
    /// (STREAMSEMBLE_PAIR_PIN_FILE, default ~/.streamsemble/pin.txt); the file is
    /// consumed once read. Falls back to the STREAMSEMBLE_PAIR_PIN env only if
    /// the file never appears (last resort — may be stale for a rolled code).
    /// </summary>
    private async Task<string> WaitForPinAsync(string? envPin, CancellationToken ct)
    {
        var pinFile = Environment.GetEnvironmentVariable("STREAMSEMBLE_PAIR_PIN_FILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".streamsemble", "pin.txt");
        if (File.Exists(pinFile))
        {
            File.Delete(pinFile); // discard any stale code before we prompt
        }

        logger.LogWarning("{Name}: pair-setup is waiting for the PIN now on the TV — write it to {File}", DisplayName, pinFile);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pinFile) && (await File.ReadAllTextAsync(pinFile, ct).ConfigureAwait(false)).Trim() is { Length: >= 4 } code)
            {
                File.Delete(pinFile);
                logger.LogInformation("{Name}: got PIN from {File}", DisplayName, pinFile);
                return code;
            }

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(envPin))
        {
            logger.LogWarning("{Name}: no PIN file within 120s — falling back to STREAMSEMBLE_PAIR_PIN", DisplayName);
            return envPin.Trim();
        }

        throw new TimeoutException($"{DisplayName}: no PIN provided within 120s (write it to {pinFile})");
    }

    private async Task<byte[]> PairVerifyAsync(HomeKitPairingStore store, HomeKitPairingStore.Accessory accessory, CancellationToken ct)
    {
        var client = new PairVerifyClient(store, accessory);
        var m2 = await PostPairAsync("/pair-verify", client.BuildM1(), VerifiedHkp, ct).ConfigureAwait(false);
        var m3 = client.HandleM2BuildM3(m2);
        var m4 = await PostPairAsync("/pair-verify", m3, VerifiedHkp, ct).ConfigureAwait(false);
        client.HandleM4(m4);
        return client.SharedSecret;
    }

    private async Task<byte[]> TransientPairAsync(CancellationToken ct)
    {
        var client = new TransientPairSetupClient();

        // pyatv posts this before pair-setup; some devices require it. Its
        // response is not needed, so failures are tolerated.
        try
        {
            await _rtsp.RequestAsync("POST", ct, "application/octet-stream", [], TransientPairHeaders, "/pair-pin-start").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "{Name}: /pair-pin-start failed (continuing)", DisplayName);
        }

        var m2 = await PostPairAsync("/pair-setup", client.BuildM1(), 4, ct).ConfigureAwait(false);
        var m3 = client.HandleM2BuildM3(m2);
        var m4 = await PostPairAsync("/pair-setup", m3, 4, ct).ConfigureAwait(false);
        client.HandleM4(m4);

        return client.SharedSecret ?? throw new InvalidOperationException("transient pairing produced no shared secret");
    }

    private System.Net.Sockets.TcpClient? _eventChannel;

    private async Task ConnectEventChannelAsync(int port, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var client = new System.Net.Sockets.TcpClient(address.AddressFamily);
                await client.ConnectAsync(address, port, ct).ConfigureAwait(false);
                _eventChannel = client;
                logger.LogInformation("{Name}: event channel connected on :{Port}", DisplayName, port);
                _ = ReadEventChannelAsync(client);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug("{Name}: event channel connect attempt {Attempt} failed ({Message})", DisplayName, attempt + 1, ex.Message);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        logger.LogWarning("{Name}: could not connect event channel on :{Port}; continuing anyway", DisplayName, port);
    }

    private HapSessionKeys? _keys;

    /// <summary>
    /// The events channel is a reverse RTSP connection: the receiver sends
    /// HAP-encrypted requests to the sender and expects 200 responses. Leaving
    /// them unanswered makes receivers drop the channel (and can gate
    /// rendering), so decrypt, log and acknowledge everything.
    /// </summary>
    private async Task ReadEventChannelAsync(System.Net.Sockets.TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var readKey = _keys!.EventsReadKey;
            var writeKey = _keys.EventsWriteKey;
            var keyResolved = false;
            ulong readCounter = 0, writeCounter = 0;
            var plaintext = new List<byte>();
            var lengthHeader = new byte[2];

            while (true)
            {
                if (!await ReadExactAsync(stream, lengthHeader).ConfigureAwait(false))
                {
                    logger.LogInformation("{Name}: event channel closed by receiver", DisplayName);
                    return;
                }

                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(lengthHeader);
                var body = new byte[length + 16];
                if (!await ReadExactAsync(stream, body).ConfigureAwait(false))
                {
                    logger.LogInformation("{Name}: event channel closed mid-frame", DisplayName);
                    return;
                }

                byte[] plain;
                var nonce = PairingCrypto.CounterNonce(readCounter);
                try
                {
                    plain = PairingCrypto.ChaCha20Poly1305Decrypt(readKey, nonce, body, lengthHeader);
                }
                catch when (!keyResolved)
                {
                    // The HKDF info names are receiver-perspective; some stacks
                    // interpret them the other way. Lock onto whichever works.
                    (readKey, writeKey) = (writeKey, readKey);
                    plain = PairingCrypto.ChaCha20Poly1305Decrypt(readKey, nonce, body, lengthHeader);
                    logger.LogInformation("{Name}: events keys were swapped relative to expectation", DisplayName);
                }

                keyResolved = true;
                readCounter++;
                plaintext.AddRange(plain);

                while (TryTakeRtspRequest(plaintext, out var requestLine, out var cseq, out var requestBody))
                {
                    var bodyNote = requestBody.Length > 0 && PropertyListParser.Parse(requestBody) is NSDictionary plist
                        ? $" plist: {plist.ToXmlPropertyList()}"
                        : requestBody.Length > 0 ? $" body {requestBody.Length} B" : "";
                    logger.LogInformation("{Name}: event rx: {Line}{Body}", DisplayName, requestLine, bodyNote);

                    var response = System.Text.Encoding.ASCII.GetBytes(
                        $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/745.83\r\nContent-Length: 0\r\n\r\n");
                    var respHeader = new byte[2];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(respHeader, (ushort)response.Length);
                    var respNonce = PairingCrypto.CounterNonce(writeCounter++);
                    var encrypted = PairingCrypto.ChaCha20Poly1305Encrypt(writeKey, respNonce, response, respHeader);
                    await stream.WriteAsync(respHeader).ConfigureAwait(false);
                    await stream.WriteAsync(encrypted).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Name}: event channel handler ended", DisplayName);
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    /// <summary>Extracts one complete RTSP/HTTP-style request from the plaintext buffer, if present.</summary>
    private static bool TryTakeRtspRequest(List<byte> buffer, out string requestLine, out string cseq, out byte[] body)
    {
        requestLine = "";
        cseq = "0";
        body = [];

        var bytes = buffer.ToArray();
        var headerEnd = -1;
        for (var i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                headerEnd = i + 4;
                break;
            }
        }

        if (headerEnd < 0)
        {
            return false;
        }

        var headerText = System.Text.Encoding.ASCII.GetString(bytes, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(value, out contentLength);
            }
            else if (name.Equals("CSeq", StringComparison.OrdinalIgnoreCase))
            {
                cseq = value;
            }
        }

        if (bytes.Length < headerEnd + contentLength)
        {
            return false; // body not fully received yet
        }

        requestLine = lines[0];
        body = bytes[headerEnd..(headerEnd + contentLength)];
        buffer.RemoveRange(0, headerEnd + contentLength);
        return true;
    }

    /// <summary>
    /// Queries /info post-pairing and returns the stream types the receiver
    /// advertises latencies for (96 = realtime supported; TVs list only
    /// 100/101/102 and need buffered).
    /// </summary>
    private async Task<HashSet<long>> LogReceiverInfoAsync(CancellationToken ct)
    {
        var types = new HashSet<long>();
        try
        {
            var response = await _rtsp.RequestAsync("GET", ct, uriOverride: "/info").ConfigureAwait(false);
            if (response.IsSuccess && response.Body.Length > 0 && PropertyListParser.Parse(response.Body) is NSDictionary info)
            {
                logger.LogDebug("{Name}: /info: {Plist}", DisplayName, info.ToXmlPropertyList());
                if (info.TryGetValue("sourceVersion", out var versionObj) && versionObj is NSString versionString
                    && double.TryParse(versionString.Content.Split('.')[0], out var version))
                {
                    _sourceVersion = version;
                }

                if (info.TryGetValue("audioLatencies", out var latenciesObj) && latenciesObj is NSArray latencies)
                {
                    foreach (var entry in latencies.OfType<NSDictionary>())
                    {
                        if (entry.TryGetValue("type", out var typeObj) && typeObj is NSNumber typeNumber)
                        {
                            types.Add(typeNumber.ToLong());
                        }
                    }
                }
            }
            else
            {
                logger.LogInformation("{Name}: GET /info returned {Status} ({Len} B)", DisplayName, response.StatusCode, response.Body.Length);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "{Name}: GET /info failed (diagnostic only)", DisplayName);
        }

        return types;
    }

    private async Task<byte[]> PostPairAsync(string path, byte[] body, int hkp, CancellationToken ct)
    {
        var response = await _rtsp.RequestAsync("POST", ct, "application/octet-stream", body, PairHeaders(hkp), path).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw new IOException($"{DisplayName}: {path} returned {response.StatusCode} {response.ReasonPhrase}");
        }

        return response.Body;
    }

    private async Task SetupAsync(int ourTimingPort, int ourControlPort, ushort startSeq, uint startRtpTime, CancellationToken ct)
    {
        // Session-level SETUP: identify ourselves and negotiate PTP timing.
        // AirPlay 2 receivers like Sonos only render with gPTP (not NTP), so we
        // advertise PTP on port 319 and provide our peer info for the BMCA.
        var localIp = (_rtsp.LocalAddress ?? IPAddress.Loopback).ToString();
        var deviceIdColon = "9F:D7:AF:12:34:56";
        var peer = new NSDictionary();
        peer.Add("Addresses", new NSArray(new NSString(localIp)));
        peer.Add("ID", new NSString(_sessionUuid.ToLowerInvariant()));
        peer.Add("DeviceType", new NSNumber(0));
        peer.Add("SupportsClockPortMatchingOverride", new NSNumber(true));

        // Full iOS/OwnTone-style session payload. The group + identity keys
        // (name, macAddress, groupUUID, groupContainsGroupLeader, peer
        // DeviceType) are what iOS sends; strict receivers (TVs) may need them
        // to treat the session as a playable group. Extra keys are harmless to
        // lenient receivers (Sonos/shairport).
        var sessionSetup = new NSDictionary();
        sessionSetup.Add("name", new NSString("Streamsemble"));
        sessionSetup.Add("deviceID", new NSString(deviceIdColon));
        sessionSetup.Add("macAddress", new NSString(deviceIdColon));
        sessionSetup.Add("sessionUUID", new NSString(_sessionUuid));
        sessionSetup.Add("groupUUID", new NSString(Guid.NewGuid().ToString().ToUpperInvariant()));
        sessionSetup.Add("groupContainsGroupLeader", new NSNumber(false));
        sessionSetup.Add("timingPort", new NSNumber(319));
        sessionSetup.Add("timingProtocol", new NSString("PTP"));
        sessionSetup.Add("timingPeerInfo", peer);
        sessionSetup.Add("timingPeerList", new NSArray(peer));
        var sessionResp = await RequestPlistAsync("SETUP", sessionSetup, ct).ConfigureAwait(false);
        logger.LogInformation("{Name}: SETUP phase 1 ok — response keys: {Keys}", DisplayName, string.Join(", ", sessionResp.Keys));

        // The device returns an event-channel port and expects the sender to
        // connect to it before the audio stream is set up (Sonos rejects the
        // stream SETUP otherwise). We only need the connection to exist; we
        // don't drive it, so a plain TCP connection with a few retries (the
        // device opens its listener slightly after responding) suffices.
        if (sessionResp.TryGetValue("eventPort", out var eventPortObj) && eventPortObj is NSNumber eventPort)
        {
            await ConnectEventChannelAsync((int)eventPort.ToLong(), ct).ConfigureAwait(false);
        }

        // RECORD ordering is the TV render gate: OwnTone/iOS send RECORD
        // immediately after the session SETUP — before SETPEERS and the stream
        // SETUP — and the TV's strict AirPlay SDK will not start reading the
        // buffered audio socket unless RECORD arrives in that position.
        // (shairport is lenient and renders either way.) Default on for buffered;
        // STREAMSEMBLE_RECORD_EARLY=0 restores the old post-stream ordering.
        var recordEarly = Environment.GetEnvironmentVariable("STREAMSEMBLE_RECORD_EARLY");
        if ((IsBuffered && recordEarly != "0") || recordEarly == "1")
        {
            await SendRecordAsync(startSeq, startRtpTime, ct).ConfigureAwait(false);
        }

        // SETPEERS carries the timing-group member list. Buffered receivers
        // (TVs) need it to start their PTP engine at all; in a multi-room group
        // it must ALSO list the OTHER receivers so every device runs BMCA over
        // the same peer set and disciplines to ONE shared grandmaster —
        // otherwise each receiver clocks to itself and cross-room audio can't
        // align. Send it whenever buffered or whenever we have >1 group member.
        var peerList = new List<string> { localIp };
        peerList.AddRange(GroupPeerAddresses.Where(a => a != localIp));
        if (IsBuffered || peerList.Count > 1)
        {
            var peers = new NSArray([.. peerList.Select(a => new NSString(a))]);
            var peersBody = BinaryPropertyListWriter.WriteToArray(peers);
            var peersResp = await _rtsp.RequestAsync("SETPEERS", ct, "/peer-list-changed", peersBody).ConfigureAwait(false);
            logger.LogInformation("{Name}: SETPEERS {Status} (peers: {Peers})", DisplayName, peersResp.StatusCode, string.Join(",", peerList));
        }

        NSDictionary stream;
        if (IsBuffered && _bufferedAlac)
        {
            // Diagnostic probe: buffered transport carrying the same ALAC
            // verbatim frames the (working) realtime path uses.
            stream = new NSDictionary();
            stream.Add("type", new NSNumber(103));
            stream.Add("ct", new NSNumber(2));                 // ALAC
            stream.Add("audioFormat", new NSNumber(0x40000));  // ALAC 44100/16/2
            stream.Add("audioMode", new NSString("default"));
            stream.Add("spf", new NSNumber(352));
            stream.Add("sr", new NSNumber(44100));
            stream.Add("shk", new NSData(_audioKeyForWire));
            stream.Add("asc", new NSData(AlacMagicCookie));
            stream.Add("supportsDynamicStreamID", new NSNumber(false));
            stream.Add("streamConnectionID", new NSNumber((long)(uint)Random.Shared.Next()));
        }
        else if (IsBuffered)
        {
            // Buffered stream (type 103): AAC-LC 44100 over TCP — what real
            // Apple senders use with TVs (confirmed by packet capture).
            stream = new NSDictionary();
            stream.Add("type", new NSNumber(103));
            stream.Add("ct", new NSNumber(4));                  // 4 = AAC-LC
            stream.Add("audioFormat", new NSNumber(0x400000));  // AAC-LC 44100/2
            stream.Add("audioMode", new NSString("default"));
            stream.Add("isMedia", new NSNumber(true));          // iOS/OwnTone send this on the stream
            stream.Add("controlPort", new NSNumber(ourControlPort));
            stream.Add("latencyMin", new NSNumber(11025));
            stream.Add("latencyMax", new NSNumber(88200));
            stream.Add("spf", new NSNumber(AacEncoderPipe.SamplesPerFrame));
            stream.Add("sr", new NSNumber(44100));
            stream.Add("shk", new NSData(_audioKeyForWire));
            stream.Add("supportsDynamicStreamID", new NSNumber(false));
            stream.Add("streamConnectionID", new NSNumber((long)(uint)Random.Shared.Next()));
        }
        else
        {
            // Realtime stream (type 96, ct=2 ALAC over UDP). The audio format
            // must be one the device advertises; Sonos supports 0x40000
            // (ALAC 44100/16/2). The shk is the key the device uses to decrypt
            // our ChaCha20 audio packets.
            stream = new NSDictionary();
            stream.Add("type", new NSNumber(0x60));            // 96 = realtime
            stream.Add("ct", new NSNumber(2));                 // 2 = ALAC
            stream.Add("audioFormat", new NSNumber(0x40000));  // ALAC 44100/16/2
            stream.Add("spf", new NSNumber(352));
            stream.Add("sr", new NSNumber(44100));
            // Pin the receiver's buffering to the group presentation latency
            // (min==max) so realtime playback lands on the same PTP instant as
            // the buffered anchor — this is what keeps multi-room in sync.
            stream.Add("latencyMin", new NSNumber(GroupPresentationLatencySamples));
            stream.Add("latencyMax", new NSNumber(GroupPresentationLatencySamples));
            stream.Add("controlPort", new NSNumber(ourControlPort));
            stream.Add("shk", new NSData(_audioKeyForWire));
            // ALAC magic cookie (ALACSpecificConfig): frameLength 352, 16-bit,
            // 2ch, 44100 Hz. Without it the receiver's ALAC decoder can't
            // initialize and plays silence, even though packets decrypt fine.
            stream.Add("asc", new NSData(AlacMagicCookie));
            stream.Add("streamConnectionID", new NSNumber((long)(uint)Random.Shared.Next()));
        }

        var streams = new NSDictionary();
        streams.Add("streams", new NSArray(stream));

        var streamResp = await RequestPlistAsync("SETUP", streams, ct).ConfigureAwait(false);
        ParseStreamResponse(streamResp);
        logger.LogInformation("{Name}: SETUP phase 2 ok — data :{Data}, control :{Control}", DisplayName, AudioEndpoint.Port, ControlEndpoint.Port);

        if (IsBuffered && !UsesLegacyBufferedFraming)
        {
            // Modern receivers (Mac→TV pcap): connect and start streaming right
            // after SETUP phase 2; RECORD follows once audio is flowing. Legacy
            // receivers (Mac→Sonos pcap) get RECORD first, stream after.
            await StartBufferedTransportAsync(ct).ConfigureAwait(false);
            await Task.Delay(600, ct).ConfigureAwait(false);
        }

        if (!_recordSent)
        {
            await SendRecordAsync(startSeq, startRtpTime, ct).ConfigureAwait(false);
        }

        if (IsBuffered && UsesLegacyBufferedFraming)
        {
            await StartBufferedTransportAsync(ct).ConfigureAwait(false);
        }
        else if (!IsBuffered)
        {
            logger.LogInformation("{Name}: AirPlay 2 realtime stream ready (audio :{Audio})", DisplayName, AudioEndpoint.Port);
        }

        // Real senders POST /feedback ~every 2 s; receivers treat it as the
        // session keep-alive and some (TVs) stop/never start rendering without it.
        _feedbackCts = new CancellationTokenSource();
        _ = FeedbackLoopAsync(_feedbackCts.Token);
    }

    private bool _recordSent;

    private async Task SendRecordAsync(ushort startSeq, uint startRtpTime, CancellationToken ct)
    {
        _recordSent = true;
        // The working Rust sender includes Range and X-Apple-ProtocolVersion and
        // this receiver acknowledges RECORD within ~15 ms; treat no answer as a
        // real problem worth surfacing rather than the expected case.
        try
        {
            using var recordCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            recordCts.CancelAfter(TimeSpan.FromSeconds(2));
            var recordResp = await _rtsp.RequestAsync("RECORD", recordCts.Token, extraHeaders: new Dictionary<string, string>
            {
                ["RTP-Info"] = $"seq={startSeq};rtptime={startRtpTime}",
                ["Range"] = "npt=0-",
                ["X-Apple-ProtocolVersion"] = "1",
            }).ConfigureAwait(false);
            logger.LogInformation("{Name}: RECORD {Status} {Reason} — headers: {Headers}", DisplayName, recordResp.StatusCode, recordResp.ReasonPhrase,
                string.Join("; ", recordResp.Headers.Select(h => $"{h.Key}: {h.Value}")));
            if (recordResp.Headers.TryGetValue("Audio-Latency", out var latencyStr) && int.TryParse(latencyStr, out var latencySamples))
            {
                ReportedAudioLatencySamples = latencySamples;
                logger.LogInformation("{Name}: receiver reports Audio-Latency {Samples} samples ({Ms:F0} ms)", DisplayName, latencySamples, latencySamples * 1000.0 / 44100);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("{Name}: no RECORD response within 2s (proceeding, but the receiver may not have started the stream)", DisplayName);
        }
    }

    private async Task StartBufferedTransportAsync(CancellationToken ct)
    {
        // Buffered audio rides a TCP connection to the negotiated dataPort.
        _bufferedCts = new CancellationTokenSource();
        _audioTcp = new System.Net.Sockets.TcpClient(address.AddressFamily) { NoDelay = true };
        await _audioTcp.ConnectAsync(address, AudioEndpoint.Port, ct).ConfigureAwait(false);
        if (!_bufferedAlac)
        {
            _aac = new AacEncoderPipe(logger);
        }

        _bufferedPump = BufferedPumpAsync(_audioTcp.GetStream(), _bufferedCts.Token);
        logger.LogInformation("{Name}: AirPlay 2 buffered stream ready (TCP audio :{Audio})", DisplayName, AudioEndpoint.Port);
    }

    // --- Buffered (type 103) transport --------------------------------------

    private const uint AacSsrc = 0x16000000;         // AAC-LC/44100/2 marker observed from real senders

    /// <summary>
    /// Receiver's AirPlay sourceVersion from /info. Older receiver SDKs
    /// (Sonos = AirTunes/366) use a different buffered TCP framing than newer
    /// ones (Philips TV = AirTunes/377): u32be self-inclusive length,
    /// header = [u32 0 ‖ rtpTime ‖ sampleRate] (no seq/SSRC), and no primer
    /// packet. Crypto envelope is identical. Confirmed by pcap of a real Mac
    /// playing buffered AAC to the Kitchen Sonos (mac-to-sonos-buffered.pcap).
    /// </summary>
    private double _sourceVersion;
    // Modern framing renders on every receiver we have (TV/AirTunes-377,
    // Sonos/AirTunes-366, shairport) once RECORD is ordered early — the
    // "old receivers need legacy u32 framing" theory came from the Mac→Sonos
    // pcap but was only ever tested before the RECORD fix, when the Sonos
    // wasn't reading at all. Real Macs do use the legacy wire format with
    // AirTunes<370, but receivers ACCEPT modern. Kept behind
    // STREAMSEMBLE_LEGACY_FRAMING=1 for reference/diagnosis.
    private bool UsesLegacyBufferedFraming =>
        Environment.GetEnvironmentVariable("STREAMSEMBLE_LEGACY_FRAMING") == "1";
    // How far ahead of "now" the anchor schedules the first frame. A live
    // source feeds at realtime, so the receiver's buffer occupancy stays at
    // exactly this value — it must exceed the receiver's start threshold or
    // playback never begins (the receiver waits forever to fill).
    // Single group-wide presentation latency: every receiver, buffered or
    // realtime, plays the sample that is "now" at (shared PTP now + this). The
    // buffered anchor maps the current frame to now+this directly; the realtime
    // stream pins its receiver latency to the SAME value (latencyMin==latencyMax)
    // so both land on the same PTP instant — no per-device trim needed. Must
    // exceed both receivers' minimum buffering (~1s AAC decode + jitter).
    public const double GroupPresentationLatencySeconds = 2.0;
    public const int GroupPresentationLatencySamples = (int)(GroupPresentationLatencySeconds * 44100);

    private AacEncoderPipe? _aac;
    private System.Net.Sockets.TcpClient? _audioTcp;
    private CancellationTokenSource? _bufferedCts;
    private Task _bufferedPump = Task.CompletedTask;
    private ulong _bufferedNonce;

    /// <summary>Set by the group: returns (PTP now in UNIX nanos, grandmaster clock id) once timing is up.</summary>
    public Func<(ulong Nanos, byte[] ClockId)?>? AnchorClock { get; set; }

    private bool _bufferedAlac;
    private readonly System.Threading.Channels.Channel<byte[]> _alacFrames =
        System.Threading.Channels.Channel.CreateBounded<byte[]>(new System.Threading.Channels.BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Live PCM in (s16le 44100 stereo); the pump encodes and ships it.</summary>
    public ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        if (_bufferedAlac)
        {
            // One PcmFrame (352 samples) per call — pack directly, no encoder.
            var packed = new byte[Raop.AlacPacker.PackedLength(pcm.Length / 4)];
            var length = Raop.AlacPacker.Pack(pcm.Span, packed);
            _alacFrames.Writer.TryWrite(packed[..length]);
            return ValueTask.CompletedTask;
        }

        return _aac?.WritePcmAsync(pcm, ct) ?? ValueTask.CompletedTask;
    }

    private async Task BufferedPumpAsync(Stream tcp, CancellationToken ct)
    {
        try
        {
            var seq = (uint)Random.Shared.Next() & 0x7FFFFF;
            var rtpTime = (uint)Random.Shared.Next();
            var anchored = false;

            if (!UsesLegacyBufferedFraming)
            {
                // Primer: real senders open the (modern-framing) stream with one
                // empty encrypted packet (ssrc 0) carrying the first frame's
                // timestamp. Legacy receivers get audio from the first packet.
                await SendBufferedPacketAsync(tcp, seq, rtpTime, 0, [], ct).ConfigureAwait(false);
                seq = (seq + 1) & 0x7FFFFF;
            }

            var reader = _bufferedAlac ? _alacFrames.Reader : _aac!.Frames;
            var samplesPerFrame = _bufferedAlac ? 352u : AacEncoderPipe.SamplesPerFrame;
            var ssrc = _bufferedAlac ? 0u : AacSsrc;
            var firstRtpTime = rtpTime;
            var framesSent = 0;

            await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Ship a chunk of audio before anchoring (receiver needs frames
                // buffered, and PTP needs to have measured the offset), then
                // anchor the CURRENT frame to now + group latency, compensated
                // by the frame's measured content age: the encoder pipeline
                // (ffmpeg stdin buffer + queue) delays frames, so the frame sent
                // "now" holds PCM captured ageSamples ago. Subtracting that age
                // aligns acoustic output with the realtime path, which sends PCM
                // the instant it arrives — no hardcoded skew.
                if (!anchored && framesSent * samplesPerFrame >= 44100)
                {
                    var ageSamples = _aac is { } aac
                        ? Math.Max(0, aac.PcmSamplesIn - (long)framesSent * samplesPerFrame)
                        : 0;
                    anchored = await SendAnchorAsync(rtpTime, ageSamples, ct).ConfigureAwait(false);
                    if (anchored && Environment.GetEnvironmentVariable("STREAMSEMBLE_TV_NUDGE") != "0")
                    {
                        await SendNowPlayingAsync(firstRtpTime, rtpTime, ct).ConfigureAwait(false);
                    }
                }

                await SendBufferedPacketAsync(tcp, seq, rtpTime, ssrc, frame, ct).ConfigureAwait(false);
                framesSent++;
                seq = (seq + 1) & 0x7FFFFF;
                rtpTime += samplesPerFrame;
                if (++_bufferedPacketsSent % 430 == 0)
                {
                    logger.LogInformation("{Name}: buffered audio: {Count} packets (~{Secs:F0}s) sent", DisplayName, _bufferedPacketsSent, _bufferedPacketsSent * 1024.0 / 44100);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: buffered audio pump stopped", DisplayName);
        }
    }

    /// <summary>
    /// Experiment: a TV with an on-screen "now playing" UI may not start its
    /// audio pipeline until the session carries progress + track metadata (a
    /// headless receiver like shairport never needs this). Sent once, just after
    /// the anchor. Gate off with STREAMSEMBLE_TV_NUDGE=0.
    /// </summary>
    private async Task SendNowPlayingAsync(uint startRtp, uint currentRtp, CancellationToken ct)
    {
        try
        {
            var end = startRtp + 44100u * 3600; // ~1h window for a live stream
            var progress = System.Text.Encoding.ASCII.GetBytes($"progress: {startRtp}/{currentRtp}/{end}\r\n");
            await _rtsp.RequestAsync("SET_PARAMETER", ct, "text/parameters", progress).ConfigureAwait(false);

            var meta = BuildDmapMetadata(album: "Diagnostics", title: "Streamsemble Test Tone", artist: "Streamsemble");
            await _rtsp.RequestAsync("SET_PARAMETER", ct, "application/x-dmap-tagged", meta).ConfigureAwait(false);
            logger.LogInformation("{Name}: sent now-playing progress + metadata (TV render nudge)", DisplayName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: now-playing SET_PARAMETER failed", DisplayName);
        }
    }

    /// <summary>Encode a DMAP (DAAP) now-playing listing item: mlit{ minm, asar, asal }.</summary>
    private static byte[] BuildDmapMetadata(string album, string title, string artist)
    {
        static byte[] Tag(string code, byte[] value)
        {
            var b = new byte[8 + value.Length];
            System.Text.Encoding.ASCII.GetBytes(code).CopyTo(b, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), value.Length);
            value.CopyTo(b, 8);
            return b;
        }

        static byte[] Str(string code, string s) => Tag(code, System.Text.Encoding.UTF8.GetBytes(s));

        var inner = new[] { Str("minm", title), Str("asar", artist), Str("asal", album) }
            .SelectMany(x => x).ToArray();
        return Tag("mlit", inner);
    }

    private async Task SendBufferedPacketAsync(Stream tcp, uint seq, uint rtpTime, uint ssrc, byte[] payload, CancellationToken ct)
    {
        var header = new byte[12];
        if (UsesLegacyBufferedFraming)
        {
            // Legacy (AirTunes < 370, e.g. Sonos): word0 is always zero and the
            // SSRC slot carries the sample rate instead.
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), rtpTime);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 44100);
        }
        else
        {
            // Byte 0 is 0x80; the next 24 bits are marker(0) + 23-bit sequence
            // (layout confirmed by packet capture of a real sender).
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, 0x80000000u | (seq & 0x7FFFFF));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), rtpTime);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), ssrc);
        }

        // Little-endian packet counter from 0 (primer included) — matches real
        // senders exactly; the receiver may derive its own counter and reject
        // anything else.
        var nonceTail = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(nonceTail, _bufferedNonce++);
        // The legacy envelope's AAD is not observable on the wire; probe modes
        // until the receiver renders: ts-sr (default, mirrors modern), none, full.
        var encrypted = Environment.GetEnvironmentVariable("STREAMSEMBLE_LEGACY_AAD") switch
        {
            "none" when UsesLegacyBufferedFraming => AudioCipher.EncryptWithAad(nonceTail, [], payload),
            "full" when UsesLegacyBufferedFraming => AudioCipher.EncryptWithAad(nonceTail, header, payload),
            _ => AudioCipher.Encrypt(nonceTail, header, payload),
        };

        // Length prefix is self-inclusive in both variants: u32be for legacy
        // receivers, u16be for modern ones.
        var lenBytes = UsesLegacyBufferedFraming ? 4 : 2;
        var packet = new byte[lenBytes + 12 + encrypted.Length];
        if (UsesLegacyBufferedFraming)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)packet.Length);
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet, (ushort)packet.Length);
        }

        header.CopyTo(packet, lenBytes);
        encrypted.CopyTo(packet.AsSpan(lenBytes + 12));
        await tcp.WriteAsync(packet, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// SETRATEANCHORTIME rate=1: "RTP timestamp X plays at network time T" on
    /// the grandmaster timeline — this is what actually starts playback.
    /// </summary>
    private async Task<bool> SendAnchorAsync(uint rtpTime, long contentAgeSamples, CancellationToken ct)
    {
        var clock = AnchorClock?.Invoke();
        if (clock is not { } anchor)
        {
            if (_anchorWaitLogged++ == 0)
            {
                logger.LogInformation("{Name}: waiting for PTP offset before SETRATEANCHORTIME", DisplayName);
            }

            return false;
        }

        // The anchored frame holds PCM captured contentAgeSamples ago (encoder
        // pipeline delay, measured live); schedule it that much earlier so PCM
        // captured at T plays at exactly T + group latency on every receiver.
        var ageNanos = (ulong)(contentAgeSamples * 1_000_000_000L / 44100);
        var nanos = anchor.Nanos + (ulong)(GroupPresentationLatencySeconds * 1_000_000_000) + (ulong)(LatencyTrimMs * 1_000_000L) - ageNanos;
        logger.LogInformation("{Name}: anchor content age {Ms:F0} ms (encoder pipeline), compensating", DisplayName, contentAgeSamples * 1000.0 / 44100);
        var fracNanos = nanos % 1_000_000_000;
        var frac64 = (ulong)(((UInt128)fracNanos << 64) / 1_000_000_000);

        var body = new NSDictionary();
        body.Add("rate", new NSNumber(1));
        body.Add("rtpTime", new NSNumber((long)rtpTime));
        body.Add("networkTimeSecs", new NSNumber((long)(nanos / 1_000_000_000)));
        body.Add("networkTimeFrac", new NSNumber(unchecked((long)frac64)));
        body.Add("networkTimeTimelineID", new NSNumber(unchecked((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(anchor.ClockId))));

        var response = await _rtsp.RequestAsync("SETRATEANCHORTIME", ct, "application/x-apple-binary-plist",
            BinaryPropertyListWriter.WriteToArray(body)).ConfigureAwait(false);
        logger.LogInformation(
            "{Name}: SETRATEANCHORTIME {Status} (rtpTime={Rtp}, secs={Secs}, timeline={Timeline})",
            DisplayName, response.StatusCode, rtpTime, nanos / 1_000_000_000, Convert.ToHexString(anchor.ClockId));
        return response.IsSuccess;
    }

    private int _anchorWaitLogged;
    private long _bufferedPacketsSent;

    private CancellationTokenSource? _feedbackCts;

    private async Task FeedbackLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var response = await _rtsp.RequestAsync("POST", ct, uriOverride: "/feedback").ConfigureAwait(false);
                if (failures == 0 && !response.IsSuccess)
                {
                    logger.LogInformation("{Name}: /feedback returned {Status}", DisplayName, response.StatusCode);
                }

                failures = 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (++failures == 1)
                {
                    logger.LogWarning(ex, "{Name}: /feedback failed", DisplayName);
                }

                if (failures > 5)
                {
                    return;
                }
            }
        }
    }

    // Raw audio key sent in the stream SETUP "shk" field (same bytes the cipher uses).
    private byte[] _audioKeyForWire = [];

    // ALACSpecificConfig for 44100/16/2, 352 frames-per-packet (matches airplay2-rs).
    private static readonly byte[] AlacMagicCookie =
    [
        0x00, 0x00, 0x01, 0x60, // frameLength = 352
        0x00,                   // compatibleVersion
        0x10,                   // bitDepth = 16
        0x28,                   // pb = 40
        0x0A,                   // mb = 10
        0x0E,                   // kb = 14
        0x02,                   // numChannels = 2
        0x00, 0xFF,             // maxRun = 255
        0x00, 0x00, 0x00, 0x00, // maxFrameBytes
        0x00, 0x00, 0x00, 0x00, // avgBitRate
        0x00, 0x00, 0xAC, 0x44, // sampleRate = 44100
    ];

    private async Task<NSDictionary> RequestPlistAsync(string method, NSDictionary body, CancellationToken ct)
    {
        var bytes = BinaryPropertyListWriter.WriteToArray(body);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
        RtspResponse response;
        try
        {
            response = await _rtsp.RequestAsync(method, timeoutCts.Token, "application/x-apple-binary-plist", bytes).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{DisplayName}: {method} got no response within 8s");
        }

        // Adopt the RTSP Session from the first SETUP response; the device
        // requires it on every subsequent request (stream SETUP, RECORD) or it
        // rejects them with 400.
        if (response.Header("Session") is { } session && !_rtsp.DefaultHeaders.ContainsKey("Session"))
        {
            _rtsp.DefaultHeaders["Session"] = session.Split(';')[0].Trim();
        }

        if (!response.IsSuccess)
        {
            var detail = response.Body.Length > 0
                ? System.Text.Encoding.ASCII.GetString(response.Body.Where(b => b >= 32 || b == 10).ToArray())
                : string.Join(", ", response.Headers.Select(h => $"{h.Key}: {h.Value}"));
            logger.LogWarning("{Name}: {Method} {Status} — headers/body: {Detail}", DisplayName, method, response.StatusCode, detail);
            throw new IOException($"{DisplayName}: {method} returned {response.StatusCode} {response.ReasonPhrase}");
        }

        return response.Body.Length > 0 && PropertyListParser.Parse(response.Body) is NSDictionary dict
            ? dict
            : new NSDictionary();
    }

    private void ParseStreamResponse(NSDictionary response)
    {
        logger.LogInformation("{Name}: SETUP(stream) response plist: {Plist}", DisplayName, response.ToXmlPropertyList());
        if (response.TryGetValue("streams", out var streamsObj) && streamsObj is NSArray { Count: > 0 } streams
            && streams[0] is NSDictionary stream)
        {
            if (stream.TryGetValue("dataPort", out var dataPort))
            {
                AudioEndpoint = new IPEndPoint(address, (int)((NSNumber)dataPort).ToLong());
            }

            if (stream.TryGetValue("controlPort", out var controlPort))
            {
                ControlEndpoint = new IPEndPoint(address, (int)((NSNumber)controlPort).ToLong());
            }

            if (stream.TryGetValue("audioBufferSize", out var bufSize) && bufSize is NSNumber size)
            {
                logger.LogInformation("{Name}: receiver audioBufferSize={Size}", DisplayName, size.ToLong());
            }
        }
    }

    private static void ExtractPort(NSDictionary dict, string key, Action<int> onPort)
    {
        if (dict.TryGetValue(key, out var value) && value is NSNumber number)
        {
            onPort((int)number.ToLong());
        }
    }

    public async Task TeardownAsync(CancellationToken ct)
    {
        try
        {
            await _rtsp.RequestAsync("TEARDOWN", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "{Name}: TEARDOWN failed", DisplayName);
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
        _bufferedCts?.Cancel();
        _aac?.Dispose();
        _audioTcp?.Dispose();
        _eventChannel?.Dispose();
        _rtsp.Dispose();
    }
}
