using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Streamsemble.AirPlay.Sender.Raop;
using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Audio;
using Streamsemble.Core.Metadata;
using Streamsemble.Discovery;
using Streamsemble.Timing;

namespace Streamsemble.AirPlay.Sender;

/// <summary>
/// The production sink: streams the live source to every selected AirPlay
/// speaker. All sessions share one master clock, one RTP timeline (identical
/// sequence numbers and timestamps), one timing responder and one control
/// channel, so the speakers align to a single timeline; per-device trim
/// offsets shift only that device's sync mapping. The selected speaker set is
/// mutable at runtime (web UI) and reconciled live.
/// </summary>
public sealed class AirPlayTargetGroup : IAudioSink, IAsyncDisposable
{
    private const int SampleRate = 44100;

    /// <summary>
    /// How far ahead of "now" the sync anchor renders audio — the receiver's
    /// buffer depth. Must sit within the SETUP latencyMin/Max (0.25–2 s); 1 s
    /// gives comfortable jitter headroom (airplay2-rs defaults to 0.2 s).
    /// </summary>
    private const double RenderDelaySeconds = 1.0;

    /// <summary>Wall-clock lead before the first packet goes out, letting the queue prime.</summary>
    private const double StartLeadSeconds = 0.25;

    private readonly IOptions<AirPlaySenderOptions> _options;
    private readonly AirPlayBrowser _browser;
    private readonly IMasterClock _clock;
    private readonly NtpTimingResponder _timingResponder;
    private readonly SelectedTargetStore _selectedTargets;
    private readonly Timing.Ptp.PtpReceiverClock _gmClock;
    private readonly ILogger<AirPlayTargetGroup> _logger;

    private readonly object _gate = new();
    // Live sessions keyed by target identity, so reconcile can add/remove
    // individual speakers while streaming. Loops read a snapshot under _gate.
    private readonly Dictionary<string, ITargetSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    // Ring holds the PLAIN RTP packets; on a retransmit we re-encrypt per the
    // requesting session, so one ring serves RAOP and AirPlay 2 targets alike.
    private readonly SequenceRingBuffer _plainRing = new(1024);   // ≈ 8 s of packets

    public AirPlayTargetGroup(
        IOptions<AirPlaySenderOptions> optionsArg,
        AirPlayBrowser browserArg,
        IMasterClock clockArg,
        NtpTimingResponder timingResponderArg,
        SelectedTargetStore selectedTargetsArg,
        Timing.Ptp.PtpReceiverClock gmClockArg,
        ILogger<AirPlayTargetGroup> loggerArg)
    {
        _options = optionsArg;
        _browser = browserArg;
        _clock = clockArg;
        _timingResponder = timingResponderArg;
        _selectedTargets = selectedTargetsArg;
        _gmClock = gmClockArg;
        _logger = loggerArg;
        _selectedTargets.Changed += (_, _) => _ = ReconcileAsync(CancellationToken.None);
    }

    /// <summary>Display names of speakers currently connected and receiving audio.</summary>
    public IReadOnlyList<string> ConnectedTargetNames
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Values.Select(s => s.DisplayName).ToList();
            }
        }
    }

    private ITargetSession[] SessionSnapshot()
    {
        lock (_gate)
        {
            return _sessions.Values.ToArray();
        }
    }

    private ControlChannel? _control;
    private UdpClient? _audioSocket;
    private Channel<PcmFrame>? _sendQueue;
    private CancellationTokenSource? _streamCts;
    private Task _sendLoop = Task.CompletedTask;

    private byte[]? _aesKey;
    private byte[]? _aesIv;
    private ushort _seq;
    private uint _rtpBase;
    private long? _timestampBase;
    private double _anchorSeconds;
    private long _anchorOffset;
    private uint _rtpHead;
    private long _packetsSent;
    private long _rateCount;
    private double _rateWindowStart;
    private bool _sendMarker;
    private bool _sendFirstSync;
    private uint _lastSyncRtp;
    private bool _syncPending;
    // Only ever sent to a speaker when the user explicitly requested a volume;
    // connecting/streaming must not change what the device is already set to.
    private float? _volume;

    private bool Streaming => _streamCts is not null;

    public async Task StartStreamAsync(AudioFormat format, CancellationToken ct = default)
    {
        if (format != AudioFormat.Canonical)
        {
            throw new NotSupportedException($"AirPlay sender expects canonical format, got {format}");
        }

        if (Streaming)
        {
            return;
        }

        _timingResponder.Start(_options.Value.TimingPort);

        _seq = (ushort)Random.Shared.Next(ushort.MaxValue);
        _rtpBase = (uint)Random.Shared.Next();
        _timestampBase = null;
        _rtpHead = _rtpBase;
        _sendMarker = true;
        _sendFirstSync = true;
        _lastSyncRtp = 0;

        _aesKey = RandomNumberGenerator.GetBytes(16);
        _aesIv = RandomNumberGenerator.GetBytes(16);

        _control = new ControlChannel(_logger);
        _control.Start(_options.Value.ControlPort, LookupRetransmit);
        _audioSocket = new UdpClient(AddressFamily.InterNetworkV6);
        _audioSocket.Client.DualMode = true;
        _audioSocket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));

        _sendQueue = Channel.CreateBounded<PcmFrame>(new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true });
        _streamCts = new CancellationTokenSource();
        _sendLoop = SendLoopAsync(_sendQueue.Reader, _streamCts.Token);

        await ReconcileAsync(ct).ConfigureAwait(false);
    }

    private static string TargetKey(AirPlayTargetOptions target)
        => target.Host is { } host ? $"host:{host}:{target.Port}" : $"name:{target.Name}";

    /// <summary>
    /// Brings the live session set in line with the current selection: connect
    /// newly-selected speakers (joining mid-stream if audio is already flowing),
    /// tear down deselected ones. Serialized so overlapping selection changes
    /// don't race. A no-op when not streaming — StartStream calls it once the
    /// shared clock/control state is ready.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await _reconcileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Streaming)
            {
                return;
            }

            var desired = _selectedTargets.Current.ToDictionary(TargetKey, t => t, StringComparer.OrdinalIgnoreCase);
            string[] liveKeys;
            lock (_gate)
            {
                liveKeys = _sessions.Keys.ToArray();
            }

            foreach (var key in liveKeys.Where(k => !desired.ContainsKey(k)))
            {
                ITargetSession? removed;
                lock (_gate)
                {
                    _sessions.Remove(key, out removed);
                }

                if (removed is not null)
                {
                    _logger.LogInformation("Dropping target {Name}", removed.DisplayName);
                    await removed.TeardownAsync(CancellationToken.None).ConfigureAwait(false);
                    removed.Dispose();
                    if (removed.RequiresPtp)
                    {
                        _gmClock.RemovePeer(removed.DeviceAddress);
                    }
                }
            }

            foreach (var (key, target) in desired)
            {
                bool present;
                lock (_gate)
                {
                    present = _sessions.ContainsKey(key);
                }

                if (present)
                {
                    continue;
                }

                // For a PTP speaker, restart the RTP timeline cleanly (fresh
                // anchor at _rtpBase, marker bit on the first packet) BEFORE
                // connecting, so RECORD, FLUSH and audio all agree on the anchor.
                // Real senders begin a fresh stream; some receivers won't render
                // a mid-stream join otherwise. Auto may resolve to AirPlay 2
                // during connect, so it gets the reset too.
                if (!target.Protocol.Equals("Raop", StringComparison.OrdinalIgnoreCase))
                {
                    _timestampBase = null;
                    _sendMarker = true;
                    _lastSyncRtp = 0;
                }

                var session = await ConnectOneAsync(target, ct).ConfigureAwait(false);
                if (session is not null)
                {
                    // Serve OUR grandmaster clock to the speaker BEFORE audio
                    // flows — it won't render until timing is established. All
                    // speakers (and inbound senders) discipline to this one
                    // clock, so every anchor timeline is ours; disciplining to
                    // a speaker's own clock only ever worked for THAT speaker
                    // (first hub tests: TV-first → only TV played, Hall-first
                    // → only Hall).
                    StartPtpIfNeeded(session);
                    if (session.RequiresPtp)
                    {
                        // Give the speaker one announce+sync round to yield its
                        // own grandmaster candidacy and lock to us.
                        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                        _logger.LogInformation("{Name}: grandmaster clock served before streaming", session.DisplayName);

                        // FLUSH to the fresh anchor right before audio starts.
                        try
                        {
                            await session.FlushAsync(_seq, _rtpBase, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogDebug(ex, "{Name}: FLUSH before streaming failed", session.DisplayName);
                        }
                    }

                    lock (_gate)
                    {
                        _sessions[key] = session;
                    }

                    // A joining speaker needs a fresh anchor (0x90) right away.
                    _sendFirstSync = true;
                    _syncPending = true;
                }
            }

            if (ConnectedTargetNames.Count == 0)
            {
                _logger.LogWarning("No AirPlay targets connected — audio is going nowhere until one is selected");
            }
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    /// <summary>Resolve every selected target's IP (best-effort) for the SETPEERS timing-group list.</summary>
    private async Task<IReadOnlyList<string>> ResolveGroupAddressesAsync(CancellationToken ct)
    {
        var addresses = new List<string>();
        foreach (var t in _selectedTargets.Current)
        {
            if (t.Host is not { } host)
            {
                continue; // name-only targets are resolved via mDNS per-session; skip for the peer list
            }

            try
            {
                var ip = IPAddress.TryParse(host, out var literal)
                    ? literal
                    : (await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false))
                        .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                        .FirstOrDefault();
                if (ip is not null)
                {
                    addresses.Add(ip.ToString());
                }
            }
            catch
            {
                // best-effort — a peer we can't resolve just won't be advertised
            }
        }

        return addresses;
    }

    // The shared hub grandmaster serves every PTP speaker (announce p1=247
    // out-ranks their own 248 candidacy; a 248 tie falls to clock-identity
    // comparison, which we can lose to both the Sonos and the TV).
    private void StartPtpIfNeeded(ITargetSession session)
    {
        if (!session.RequiresPtp)
        {
            return;
        }

        if (!_gmClock.EnsureStarted())
        {
            _logger.LogError("PTP clock could not start (ports 319/320 need root/CAP_NET_BIND_SERVICE); {Name} will not render audio", session.DisplayName);
            return;
        }

        _gmClock.AddPeer(session.DeviceAddress, priority1: 247);
    }

    private async Task<ITargetSession?> ConnectOneAsync(AirPlayTargetOptions target, CancellationToken ct)
    {
        try
        {
            var scanTime = TimeSpan.FromSeconds(_options.Value.ScanSeconds);
            IPAddress address;
            var port = target.Port;
            var name = target.Name ?? target.Host!;
            IReadOnlyDictionary<string, string>? txt = null;
            int? airPlayPort = null;
            var protocol = target.Protocol;

            if (target.Host is { } host)
            {
                address = IPAddress.TryParse(host, out var literal)
                    ? literal
                    : (await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false))
                        .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                        .First();

                // Auto + host-only config: try to find the device's mDNS record
                // by address so the feature bits can pick the protocol.
                if (protocol.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                {
                    var seen = await _browser.BrowseAsync(scanTime, ct).ConfigureAwait(false);
                    if (seen.FirstOrDefault(t => t.Address.Equals(address)) is { } match)
                    {
                        (txt, airPlayPort) = (match.RaopTxt, match.AirPlayPort);
                        protocol = match.SupportsAirPlay2 ? "AirPlay2" : "Raop";
                        _logger.LogInformation("{Name}: protocol auto-detected as {Protocol} (features 0x{Features:X})", name, protocol, match.Features);
                    }
                    else
                    {
                        protocol = "Raop";
                        _logger.LogWarning("{Name}: no mDNS record found for {Address}; Auto is falling back to RAOP — set Protocol explicitly if this is an AirPlay 2 device", name, address);
                    }
                }
            }
            else
            {
                var discovered = await _browser.BrowseAsync(scanTime, ct).ConfigureAwait(false);
                var resolved = discovered.FirstOrDefault(t => t.DisplayName.Contains(target.Name!, StringComparison.OrdinalIgnoreCase))
                    ?? throw new IOException($"\"{target.Name}\" not found via mDNS (saw: {string.Join(", ", discovered.Select(d => d.DisplayName).DefaultIfEmpty("nothing"))})");
                (address, port, txt, name, airPlayPort) = (resolved.Address, resolved.RaopPort, resolved.RaopTxt, resolved.DisplayName, resolved.AirPlayPort);
                if (protocol.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                {
                    protocol = resolved.SupportsAirPlay2 ? "AirPlay2" : "Raop";
                    _logger.LogInformation("{Name}: protocol auto-detected as {Protocol} (features 0x{Features:X})", name, protocol, resolved.Features);
                }
            }

            // Joiners get the current send position so the RECORD hint is close;
            // the per-second sync packets re-anchor them precisely regardless.
            var startRtp = _timestampBase is null ? _rtpBase : _rtpHead;

            ITargetSession session;
            if (protocol.Equals("AirPlay2", StringComparison.OrdinalIgnoreCase))
            {
                // AirPlay 2 RTSP rides the _airplay port (typically 7000), not
                // the RAOP port; 5000 is the config default meaning "unset".
                var rtspPort = airPlayPort ?? (target.Port == 5000 ? 7000 : port);
                var ap2 = new AirPlay2.AirPlay2Session(name, address, rtspPort, target.LatencyTrimMs, _logger, target.StreamMode)
                {
                    // Timing-group peer list: all selected targets' addresses, so
                    // a multi-room group shares one PTP grandmaster (SETPEERS).
                    GroupPeerAddresses = await ResolveGroupAddressesAsync(ct).ConfigureAwait(false),
                };
                // Buffered playback anchors to OUR grandmaster clock via
                // SETRATEANCHORTIME once the first frame is ready.
                ap2.AnchorClock = () => ((ulong)Timing.Ptp.PtpReceiverClock.NowNanos, _gmClock.ClockId);
                await ap2.ConnectAsync(_timingResponder.Port, _control!.Port, _seq, startRtp, ct).ConfigureAwait(false);
                session = ap2;
            }
            else
            {
                var encrypted = target.Encryption.ToLowerInvariant() switch
                {
                    "rsa" => true,
                    "none" => false,
                    // Auto: prefer unencrypted when the device allows it (et contains 0).
                    _ => txt is not null
                         && txt.TryGetValue("et", out var et)
                         && !et.Split(',').Contains("0")
                         && et.Split(',').Contains("1"),
                };

                var raop = new RaopSession(name, address, port, encrypted, target.LatencyTrimMs, _logger);
                await raop.ConnectAsync(_aesKey, _aesIv, _control!.Port, _timingResponder.Port, _seq, startRtp, ct).ConfigureAwait(false);
                session = raop;
            }

            if (_volume is { } requestedVolume)
            {
                // Match the group volume the user chose earlier; otherwise leave
                // the device at whatever it's already set to.
                await session.SetVolumeAsync(requestedVolume, ct).ConfigureAwait(false);
            }

            return session;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to connect AirPlay target {Target}", target.Name ?? target.Host);
            return null;
        }
    }

    public async ValueTask WriteAsync(PcmFrame frame, CancellationToken ct = default)
    {
        if (_sendQueue is { } queue)
        {
            await queue.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
        }
    }

    private async Task SendLoopAsync(ChannelReader<PcmFrame> queue, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in queue.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_timestampBase is null)
                {
                    _timestampBase = frame.Timestamp;
                    _anchorOffset = 0;
                    _anchorSeconds = _clock.NowSeconds + StartLeadSeconds;
                }

                var offset = frame.Timestamp - _timestampBase.Value;
                var rtpTime = (uint)(_rtpBase + offset);

                // Pace against the master clock: packet with sample-offset N is
                // due at anchor + (N - anchorOffset)/rate.
                var due = _anchorSeconds + (offset - _anchorOffset) / (double)SampleRate;
                var wait = due - _clock.NowSeconds;
                if (wait > 0.002)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
                }

                var header = BuildRtpHeader(_seq, rtpTime, _sendMarker);
                _sendMarker = false;
                var pcm = frame.Data.ToArray();
                // Ring stores header+PCM; each target re-frames it on retransmit.
                var plain = new byte[12 + pcm.Length];
                header.CopyTo(plain, 0);
                pcm.CopyTo(plain, 12);
                _plainRing.Add(_seq, plain);

                // Anchor playback the way OwnTone/airplay2-rs do: send a sync
                // packet (this RTP time renders at now + RenderDelay) with the
                // first packet, whenever a new target joins, and ~once a second.
                if (_syncPending || _lastSyncRtp == 0 || rtpTime - _lastSyncRtp >= (uint)SampleRate)
                {
                    var first = _sendFirstSync;
                    _sendFirstSync = false;
                    _syncPending = false;
                    _lastSyncRtp = rtpTime;
                    var nextRtp = (uint)(rtpTime + PcmFrame.SamplesPerFrame);
                    foreach (var session in SessionSnapshot())
                    {
                        if (session is AirPlay2.AirPlay2Session { IsBuffered: true })
                        {
                            // Buffered receivers are anchored once via
                            // SETRATEANCHORTIME and follow PTP; no sync packets.
                            continue;
                        }

                        if (session.RequiresPtp)
                        {
                            // PTP anchor semantics (per the working Rust sender):
                            // "this RTP timestamp corresponds to PTP-clock NOW" —
                            // the receiver adds its own negotiated latency. Only
                            // the per-device trim shifts the mapping.
                            var anchorNanos = (ulong)(Timing.Ptp.PtpReceiverClock.NowNanos + session.LatencyTrimMs * 1_000_000L);
                            await _control!.SendPtpSyncAsync(session.ControlEndpoint, rtpTime, anchorNanos, nextRtp, _gmClock.ClockId, first, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            // Positive per-device trim renders that speaker later.
                            var renderNtp = _clock.NtpInSeconds(RenderDelaySeconds + session.LatencyTrimMs / 1000.0);
                            await _control!.SendSyncAsync(session.ControlEndpoint, rtpTime, renderNtp, first, ct).ConfigureAwait(false);
                        }
                    }
                }

                foreach (var session in SessionSnapshot())
                {
                    // Buffered sessions consume raw PCM; they encode (AAC),
                    // packetize and ship over their own TCP connection.
                    if (session is AirPlay2.AirPlay2Session { IsBuffered: true } bufferedSession)
                    {
                        try
                        {
                            await bufferedSession.WritePcmAsync(pcm, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "{Name}: buffered PCM write failed", session.DisplayName);
                        }

                        continue;
                    }

                    // Each target frames the shared header+PCM its own way
                    // (RAOP AES-CBC ALAC vs AirPlay 2 ChaCha20 PCM), same seq/timestamp.
                    var bytes = session.PrepareWirePacket(header, pcm, _seq, rtpTime);
                    if (_packetsSent == 0)
                    {
                        var rms = PcmRms(pcm);
                        _logger.LogInformation(
                            "DIAG first audio packet: dest={Dest}, wire_len={Len}, seq={Seq}, rtp_ts={Rtp}, marker={Marker}, pcm_rms={Rms:F1}, header_first4={Header}",
                            session.AudioEndpoint, bytes.Length, _seq, rtpTime, (header[1] & 0x80) != 0, rms, Convert.ToHexString(bytes.AsSpan(0, 4)));
                    }
                    try
                    {
                        var sent = await _audioSocket!.SendAsync(bytes, session.AudioEndpoint, ct).ConfigureAwait(false);
                        _packetsSent++;
                        _rateCount++;
                        var elapsed = _clock.NowSeconds - _rateWindowStart;
                        if (elapsed >= 1.0)
                        {
                            _logger.LogInformation("Audio rate: {Rate:F0} pkt/s to {Name} ({Bytes} B/pkt) — realtime is ~125", _rateCount / elapsed, session.DisplayName, sent);
                            _rateCount = 0;
                            _rateWindowStart = _clock.NowSeconds;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "{Name}: audio send failed", session.DisplayName);
                    }

                    session.NoteRtpTime(rtpTime);
                }

                _seq++;
                _rtpHead = (uint)(rtpTime + frame.SampleCount);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AirPlay send loop failed");
        }
    }

    private static double PcmRms(ReadOnlySpan<byte> pcmS16Le)
    {
        double sum = 0;
        var samples = pcmS16Le.Length / 2;
        for (var i = 0; i < pcmS16Le.Length; i += 2)
        {
            double s = (short)(pcmS16Le[i] | pcmS16Le[i + 1] << 8);
            sum += s * s;
        }

        return samples == 0 ? 0 : Math.Sqrt(sum / samples);
    }

    private static byte[] BuildRtpHeader(ushort seq, uint rtpTime, bool marker)
    {
        var header = new byte[12];
        header[0] = 0x80;
        header[1] = (byte)(0x60 | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), rtpTime);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0); // SSRC 0 (matches real senders)
        return header;
    }

    private byte[]? LookupRetransmit(ushort seq, IPEndPoint requester)
    {
        if (!_plainRing.TryGet(seq, out var plain))
        {
            return null;
        }

        // Re-frame the cached header+PCM for whichever target is asking. The
        // dual-mode control socket reports IPv4 peers as IPv4-mapped IPv6, so
        // normalize before comparing against the session's IPv4 endpoint.
        var requesterAddress = requester.Address.IsIPv4MappedToIPv6 ? requester.Address.MapToIPv4() : requester.Address;
        var session = SessionSnapshot().FirstOrDefault(s =>
        {
            var a = s.ControlEndpoint.Address;
            return (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(requesterAddress);
        });
        if (session is null)
        {
            return null;
        }

        var header = plain[..12];
        var pcm = plain[12..];
        var rtpTime = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        return session.PrepareWirePacket(header, pcm, seq, rtpTime);
    }

    public async Task SetVolumeAsync(float volume, CancellationToken ct = default)
    {
        _volume = volume;
        foreach (var session in SessionSnapshot())
        {
            try
            {
                await session.SetVolumeAsync(volume, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Name}: volume update failed", session.DisplayName);
            }
        }
    }

    public async Task SetMetadataAsync(TrackMetadata metadata, CancellationToken ct = default)
    {
        foreach (var session in SessionSnapshot())
        {
            await session.SetMetadataAsync(metadata, ct).ConfigureAwait(false);
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (!Streaming || _timestampBase is null)
        {
            _logger.LogDebug("flush skipped (streaming={Streaming}, anchored={Anchored})", Streaming, _timestampBase is not null);
            return;
        }

        // Reset the timeline FIRST — the next packet re-anchors and re-marks
        // the stream regardless of how the per-speaker flushes fare. Holding
        // this hostage to every speaker's RTSP responsiveness meant one slow
        // receiver silently kept the whole group on the stale timeline.
        _timestampBase = null;
        _sendMarker = true;
        _sendFirstSync = true;
        _syncPending = true;
        _lastSyncRtp = 0;

        var sessions = SessionSnapshot();
        _logger.LogInformation("flushing {Count} target(s) (drop buffered audio, re-anchor)", sessions.Length);
        await Task.WhenAll(sessions.Select(async session =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await session.FlushAsync(_seq, _rtpHead, timeout.Token).ConfigureAwait(false);
                _logger.LogDebug("{Name}: flushed", session.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name}: flush failed", session.DisplayName);
            }
        })).ConfigureAwait(false);
    }

    public async Task StopStreamAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _streamCts;
            _streamCts = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            await _sendLoop.ConfigureAwait(false);
        }
        catch
        {
            // Loop errors already logged.
        }

        foreach (var session in SessionSnapshot())
        {
            await session.TeardownAsync(CancellationToken.None).ConfigureAwait(false);
            session.Dispose();
        }

        lock (_gate)
        {
            _sessions.Clear();
        }

        _control?.Dispose();
        _control = null;
        foreach (var session in SessionSnapshot())
        {
            if (session.RequiresPtp)
            {
                _gmClock.RemovePeer(session.DeviceAddress);
            }
        }
        _audioSocket?.Dispose();
        _audioSocket = null;
        _sendQueue = null;
        _plainRing.Clear();
        cts.Dispose();
        _logger.LogInformation("AirPlay stream stopped");
    }

    public async ValueTask DisposeAsync() => await StopStreamAsync().ConfigureAwait(false);
}
