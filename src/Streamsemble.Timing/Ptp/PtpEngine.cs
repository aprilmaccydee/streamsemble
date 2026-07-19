using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Streamsemble.Timing.Ptp;

/// <summary>
/// gPTP timing for an AirPlay 2 receiver that requires PTP. Acts like a Mac
/// sender: announces as a master candidate on 319/320 and runs BMCA. Two
/// outcomes: a grandmaster-capable receiver (Sonos) wins with priority1=248 and
/// we yield to slave mode; a slave-only receiver (TVs) never announces, so we
/// stay grandmaster and keep serving Sync/Follow_Up/Announce and answering
/// Delay_Req. The audio anchor uses <see cref="NowMasterNanos"/> either way.
///
/// Requires binding privileged UDP ports 319/320 (run as root / with
/// CAP_NET_BIND_SERVICE), and the receiver must be able to reach those ports.
/// </summary>
public sealed class PtpEngine : IDisposable
{
    private const byte OurPriority1 = 250; // Mac's value; the receiver's 248 wins, so we go slave

    private readonly ILogger _logger;
    private readonly IPAddress _masterIp;
    private readonly byte[] _clockId;
    private readonly object _gate = new();

    private PtpPortMux? _mux;
    private PtpPortMux.Consumer? _consumer;
    private CancellationTokenSource? _cts;

    // Shared slave-loop state (guarded by _gate).
    private long? _t1; // master origin (Follow_Up)
    private long? _t2; // our receive of Sync
    private long? _t3; // our send of Delay_Req
    private ushort _delayReqSeq;
    private int _eventLog;
    private int _generalLog;

    private static readonly long BaseUnixNanos = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
    private static readonly long BaseTicks = Stopwatch.GetTimestamp();
    private static readonly double NanosPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    public PtpEngine(IPAddress masterIp, ILogger logger)
    {
        _masterIp = masterIp;
        _logger = logger;
        _clockId = BitConverter.GetBytes((ulong)NowUnixNanos());
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(_clockId); // big-endian clock identity
        }
    }

    public long OffsetNanos { get; private set; }

    // Sliding window of raw one-way offset samples; see the min-filter note in
    // GeneralLoopAsync. ~4 s of samples at the 8 Hz Sync cadence.
    private const int OffsetWindowSize = 32;
    private readonly Queue<long> _offsetWindow = new();
    public bool IsSynced { get; private set; }
    public byte[]? MasterClockId { get; private set; }

    private int _syncCount;

    /// <summary>
    /// Waits until BMCA has resolved the grandmaster (so the audio sync can
    /// carry its clock id). A full delay-offset lock isn't required — like a
    /// real sender, we anchor to the master's UTC-based clock with offset 0
    /// until/unless a Delay_Resp refines it.
    /// </summary>
    public async Task<bool> WaitForSyncAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (MasterClockId is null && Stopwatch.GetTimestamp() < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        return MasterClockId is not null;
    }

    /// <summary>
    /// The clock time to stamp into audio PTP sync packets, in UNIX
    /// nanoseconds. We deliberately DON'T apply the measured offset: real
    /// senders (airplay2-rs/OwnTone) run the BMCA + slave exchange but send
    /// their own local UTC clock (offset 0) in the audio anchor, and the
    /// receiver renders from that. Applying the offset (jumping to the
    /// receiver's uptime clock) actually breaks rendering.
    /// </summary>
    public long NowMasterNanos => NowUnixNanos();

    /// <summary>
    /// "Now" expressed in the grandmaster's own clock domain — required by
    /// SETRATEANCHORTIME, which receivers validate against their PTP clock
    /// (a TV's grandmaster clock is its uptime; a UNIX-epoch anchor lands
    /// decades in the future and the receiver aborts the session). Null until
    /// the offset is measured; zero offset when we are the grandmaster.
    /// </summary>
    public long? AnchorNanos => IsSynced ? NowUnixNanos() - OffsetNanos : null;

    public static long NowUnixNanos() => BaseUnixNanos + (long)((Stopwatch.GetTimestamp() - BaseTicks) * NanosPerTick);

    /// <summary>Starts the timing flow over the shared PTP ports. Throws if they can't be bound.</summary>
    public void Start()
    {
        _mux = PtpPortMux.GetShared(_logger);
        if (!_mux.EnsureBound())
        {
            throw new InvalidOperationException(
                $"cannot bind UDP {PtpWire.EventPort}/{PtpWire.GeneralPort} for PTP");
        }

        _consumer = _mux.RegisterEngineRole();
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
        _logger.LogInformation("PTP engine started toward master {Master} (event :{E}, general :{G})", _masterIp, PtpWire.EventPort, PtpWire.GeneralPort);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var eventDest = new IPEndPoint(_masterIp, PtpWire.EventPort);
            var generalDest = new IPEndPoint(_masterIp, PtpWire.GeneralPort);
            ushort syncSeq = 0, announceSeq = 0, signalingSeq = 0;

            // Phase 1: announce as a master candidate (Mac cadence).
            for (var i = 0; i < 3; i++)
            {
                await SendSyncPairAsync(eventDest, generalDest, ++syncSeq, ct).ConfigureAwait(false);
                if (i < 2)
                {
                    await _mux!.SendGeneralAsync(PtpWire.BuildAnnounce(++announceSeq, _clockId, 248, OurPriority1), generalDest, ct).ConfigureAwait(false);
                }

                await Task.Delay(125, ct).ConfigureAwait(false);
            }

            await _mux!.SendGeneralAsync(PtpWire.BuildMacSignaling(++signalingSeq, _clockId), generalDest, ct).ConfigureAwait(false);

            // Phase 2: wait for the receiver's Announce to learn its priority + clock id.
            var (remotePriority1, remoteClockId) = await AwaitRemoteAnnounceAsync(ct).ConfigureAwait(false);

            // Phase 3: BMCA — lower priority1 wins. Grandmaster receivers (Sonos)
            // announce with 248 and win; slave-only receivers (TVs) never
            // announce, so we stay master and must keep serving the clock.
            var weAreMaster = OurPriority1 < remotePriority1;
            _logger.LogInformation("PTP BMCA: our_p1={Ours}, remote_p1={Remote}, weAreMaster={Master}", OurPriority1, remotePriority1, weAreMaster);
            if (weAreMaster)
            {
                MasterClockId = _clockId;
                _logger.LogInformation("PTP: acting as grandmaster for {Peer} (clock id {Clock})", _masterIp, Convert.ToHexString(_clockId));
                IsSynced = true; // our own clock is trivially "synced" (offset 0)
                await Task.WhenAll(
                    MasterAnnounceLoopAsync(eventDest, generalDest, syncSeq, announceSeq, ct),
                    MasterEventLoopAsync(generalDest, ct)).ConfigureAwait(false);
                return;
            }

            MasterClockId = remoteClockId;
            await _mux!.SendGeneralAsync(PtpWire.BuildStopSignaling(++signalingSeq, _clockId), generalDest, ct).ConfigureAwait(false);
            _logger.LogInformation("PTP: yielded master, entering slave sync to {Master}", _masterIp);

            // Phase 4 (slave): consume the master's clock across both sockets.
            // Watchdog: some receivers (e.g. shairport-sync/nqptp) announce a
            // winning priority but never actually serve Sync — they expect the
            // sender to be the clock. If sync doesn't complete quickly, reclaim
            // the grandmaster role and drive the clock ourselves. Real speakers
            // (Sonos, TVs) send Sync within ~125 ms, so this never fires for them
            // and the working realtime path is unaffected.
            using var slaveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var slaveTask = Task.WhenAll(
                EventLoopAsync(eventDest, slaveCts.Token),
                GeneralLoopAsync(eventDest, slaveCts.Token));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!IsSynced && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            if (!IsSynced && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("PTP: master {Master} announced but served no Sync within 3s — reclaiming grandmaster role", _masterIp);
                slaveCts.Cancel();
                try { await slaveTask.ConfigureAwait(false); } catch { /* slave loops cancelled */ }

                MasterClockId = _clockId;
                IsSynced = true; // our own clock is trivially "synced" (offset 0)
                await Task.WhenAll(
                    MasterAnnounceLoopAsync(eventDest, generalDest, syncSeq, announceSeq, ct),
                    MasterEventLoopAsync(generalDest, ct)).ConfigureAwait(false);
                return;
            }

            await slaveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PTP engine failed");
        }
    }

    private async Task SendSyncPairAsync(IPEndPoint eventDest, IPEndPoint generalDest, ushort seq, CancellationToken ct)
    {
        await _mux!.SendEventAsync(PtpWire.BuildSync(seq, _clockId), eventDest, ct).ConfigureAwait(false);
        var now = NowUnixNanos();
        await _mux!.SendGeneralAsync(PtpWire.BuildFollowUp(seq, _clockId, (ulong)(now / 1_000_000_000L), (uint)(now % 1_000_000_000L)), generalDest, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Master mode: keep serving Sync/Follow_Up (Mac cadence ~125 ms) and
    /// Announce (~1 s) so a slave-only receiver (TV) can discipline to us.
    /// </summary>
    private async Task MasterAnnounceLoopAsync(IPEndPoint eventDest, IPEndPoint generalDest, ushort syncSeq, ushort announceSeq, CancellationToken ct)
    {
        var iteration = 0;
        while (!ct.IsCancellationRequested)
        {
            await SendSyncPairAsync(eventDest, generalDest, ++syncSeq, ct).ConfigureAwait(false);
            if (iteration++ % 8 == 0)
            {
                await _mux!.SendGeneralAsync(PtpWire.BuildAnnounce(++announceSeq, _clockId, 248, OurPriority1), generalDest, ct).ConfigureAwait(false);
            }

            await Task.Delay(125, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Master mode: answer the slave's Delay_Req so it can measure path delay.</summary>
    private async Task MasterEventLoopAsync(IPEndPoint generalDest, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PtpPortMux.PtpPacket packet;
            try
            {
                packet = await _consumer!.Event.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch when (ct.IsCancellationRequested) { return; }
            catch (ChannelClosedException) { return; }

            var received = packet.ReceivedNanos;
            var type = PtpWire.ParseType(packet.Data);
            var from = packet.From;
            if (_eventLog++ < 8)
            {
                _logger.LogInformation("PTP master rx: {Type} from {From} ({Len} B)", type, from, packet.Data.Length);
            }

            if (!from.Equals(_masterIp) || type != PtpMessageType.DelayReq || packet.Data.Length < 34)
            {
                continue;
            }

            var resp = PtpWire.BuildDelayResp(packet.Data, _clockId, (ulong)(received / 1_000_000_000L), (uint)(received % 1_000_000_000L));
            try
            {
                await _mux!.SendGeneralAsync(resp, generalDest, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "PTP: Delay_Resp send failed");
            }
        }
    }

    private async Task<(byte Priority1, byte[] ClockId)> AwaitRemoteAnnounceAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var packet = await _consumer!.General.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (packet.From.Equals(_masterIp)
                    && packet.Data.Length >= 61
                    && PtpWire.ParseType(packet.Data) == PtpMessageType.Announce)
                {
                    return (packet.Data[47], packet.Data[53..61]);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("PTP: no remote Announce within 3s; proceeding as slave anyway");
        }

        return (255, new byte[8]);
    }

    private async Task EventLoopAsync(IPEndPoint eventDest, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PtpPortMux.PtpPacket packet;
            try
            {
                packet = await _consumer!.Event.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch when (ct.IsCancellationRequested) { return; }
            catch (ChannelClosedException) { return; }

            var type = PtpWire.ParseType(packet.Data);
            var from = packet.From;
            if (_eventLog++ < 8)
            {
                _logger.LogInformation("PTP event rx: {Type} from {From} ({Len} B), matchMaster={Match}", type, from, packet.Data.Length, from.Equals(_masterIp));
            }

            if (!from.Equals(_masterIp))
            {
                continue;
            }

            switch (type)
            {
                case PtpMessageType.Sync:
                    lock (_gate)
                    {
                        _t2 = packet.ReceivedNanos;
                    }

                    break;
                case PtpMessageType.DelayResp when packet.Data.Length >= 44:
                    var (s, n) = PtpWire.ParseTimestamp(packet.Data.AsSpan(34, 10));
                    var t4 = (long)PtpWire.TimestampToNanos(s, n);
                    ComputeOffset(t4);
                    break;
                case PtpMessageType.DelayResp:
                    _logger.LogDebug("PTP: Delay_Resp too short ({Len} bytes)", packet.Data.Length);
                    break;
            }
        }
    }

    private async Task GeneralLoopAsync(IPEndPoint eventDest, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PtpPortMux.PtpPacket packet;
            try
            {
                packet = await _consumer!.General.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch when (ct.IsCancellationRequested) { return; }
            catch (ChannelClosedException) { return; }

            var gtype = PtpWire.ParseType(packet.Data);
            var gfrom = packet.From;
            if (_generalLog++ < 8)
            {
                _logger.LogInformation("PTP general rx: {Type} from {From} ({Len} B), matchMaster={Match}", gtype, gfrom, packet.Data.Length, gfrom.Equals(_masterIp));
            }

            if (!gfrom.Equals(_masterIp) || packet.Data.Length < 44 || gtype != PtpMessageType.FollowUp)
            {
                continue;
            }

            var (s, n) = PtpWire.ParseTimestamp(packet.Data.AsSpan(34, 10));
            long t3;
            ushort seq;
            lock (_gate)
            {
                var t1 = (long)PtpWire.TimestampToNanos(s, n);
                _t1 = t1;

                // One-way offset from the Sync/Follow_Up pair: offset = t2 - t1
                // (our receive time minus the master's send time) ≈ our_clock -
                // master_clock. The raw sample carries up to ~100 ms of
                // userspace receive jitter, and that jitter is strictly
                // POSITIVE (a late wakeup only ever makes t2 later) — so the
                // best estimate over a window is the MINIMUM sample, which
                // approaches the true offset + sub-ms path delay. Without this,
                // each buffered anchor bakes in one noisy sample and playback
                // alignment shifts ±100 ms run to run.
                if (_t2 is { } t2)
                {
                    _offsetWindow.Enqueue(t2 - t1);
                    while (_offsetWindow.Count > OffsetWindowSize)
                    {
                        _offsetWindow.Dequeue();
                    }

                    OffsetNanos = _offsetWindow.Min();
                    var wasSynced = IsSynced;
                    IsSynced = true;
                    if (!wasSynced || ++_syncCount % 100 == 0)
                    {
                        _logger.LogInformation("PTP synced (one-way): offset={Offset} ns (min of {Count} samples, raw spread {Spread} ms)",
                            OffsetNanos, _offsetWindow.Count, (_offsetWindow.Max() - _offsetWindow.Min()) / 1_000_000);
                    }
                }

                t3 = NowUnixNanos();
                _t3 = t3;
                seq = ++_delayReqSeq;
            }

            var delayReq = PtpWire.BuildDelayReq(seq, _clockId, (ulong)(t3 / 1_000_000_000L), (uint)(t3 % 1_000_000_000L));
            try
            {
                await _mux!.SendEventAsync(delayReq, eventDest, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "PTP: Delay_Req send failed");
            }
        }
    }

    private void ComputeOffset(long t4)
    {
        lock (_gate)
        {
            if (_t1 is not { } t1 || _t2 is not { } t2 || _t3 is not { } t3)
            {
                return;
            }

            // offset = ((t2 - t1) + (t3 - t4)) / 2  =  our_clock - master_clock
            OffsetNanos = ((t2 - t1) + (t3 - t4)) / 2;
            var wasSynced = IsSynced;
            IsSynced = true;
            _t1 = _t2 = _t3 = null;
            if (!wasSynced || ++_syncCount % 50 == 0)
            {
                _logger.LogInformation("PTP synced: offset={Offset} ns, delay={Delay} ns", OffsetNanos, ((t2 - t1) - (t3 - t4)) / 2);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_consumer is { } consumer)
        {
            _mux?.UnregisterEngineRole(consumer);
        }
    }
}
