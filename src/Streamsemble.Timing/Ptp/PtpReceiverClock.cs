using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Streamsemble.Timing.Ptp;

/// <summary>
/// The receiver-role PTP clock, mirroring the living-room TV's grandmaster
/// behavior from the mac-to-tv capture — the empirically proven shape for a
/// real Mac sender:
///
/// 1. Ports 319/320 must be BOUND: with nothing there, the Mac's PTP bounces
///    as ICMP port-unreachable, no clock relationship forms, and it tears the
///    session down after ~20 s (round 3).
/// 2. The receiver must actively WIN the master election and serve the clock:
///    the Mac announces priority1=250; the TV out-announces it at 248, serves
///    Sync/Follow_Up @8 Hz + Announce+Signaling @1 s, answers Delay_Req — and
///    only ~1.5 s after that takeover does the Mac open the audio stream. A
///    bound-but-silent (nqptp-style) receiver keeps the session alive but the
///    Mac never opens the stream (round 8): a master with no evidence of any
///    clock participant on the other end doesn't hand audio over.
///
/// (Round 4 looked like the Mac rejecting our grandmaster, which prompted a
/// passive detour — that run was actually poisoned by a stale process holding
/// 319/320 on the SENDER's machine, which aborts its sessions instantly.
/// Nothing may hold those ports on a Mac that sends AirPlay.)
///
/// One instance per process; peers are refcounted so overlapping sessions
/// from the same sender don't tear the clock from under each other. The
/// ports themselves are owned by <see cref="PtpPortMux"/>, which routes
/// packets from tracked sender peers here and everything else to the
/// sender-side <see cref="PtpEngine"/> — both roles run at once on the hub.
/// </summary>
public sealed class PtpReceiverClock(byte[] clockId, ILogger logger) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, (int Count, byte Priority1)> _peers = [];
    private PtpPortMux? _mux;
    private PtpPortMux.Consumer? _consumer;
    private CancellationTokenSource? _cts;
    private bool _started;
    private int _rxLog;
    private int _delayReqLog;

    public byte[] ClockId { get; } = clockId;

    /// <summary>Now in the clock domain we serve (UNIX nanos, same base the sender engine uses).</summary>
    public static long NowNanos => PtpEngine.NowUnixNanos();

    /// <summary>Starts the clock over the shared PTP ports (idempotent). False if they can't be bound.</summary>
    public bool EnsureStarted()
    {
        lock (_gate)
        {
            if (_started)
            {
                return true;
            }

            _mux = PtpPortMux.GetShared(logger);
            if (!_mux.EnsureBound())
            {
                logger.LogError(
                    "PTP clock: shared ports unavailable — senders will not stream without a clock");
                return false;
            }

            _consumer = _mux.RegisterReceiverRole(OwnsPeer);
            _cts = new CancellationTokenSource();
            _ = ServeLoopAsync(_cts.Token);
            _ = EventRxLoopAsync(_cts.Token);
            _ = GeneralRxLoopAsync(_cts.Token);
            _started = true;
            logger.LogInformation("PTP clock serving on :{E}/:{G} (grandmaster, clock id {Clock})",
                PtpWire.EventPort, PtpWire.GeneralPort, Convert.ToHexString(ClockId));
            return true;
        }
    }

    private bool OwnsPeer(IPAddress address)
    {
        lock (_gate)
        {
            return _peers.ContainsKey(PtpPortMux.Normalize(address));
        }
    }

    /// <summary>
    /// Track a peer this clock serves. Inbound AirPlay senders take the
    /// default 248 (TV-exact); speaker targets pass 247 so our announce
    /// out-ranks their own 248 grandmaster candidacy (a 248 tie falls to
    /// clock-identity comparison, which we can lose).
    /// </summary>
    public void AddPeer(IPAddress address, byte priority1 = 248)
    {
        var peer = Normalize(address);
        lock (_gate)
        {
            _peers.TryGetValue(peer, out var entry);
            _peers[peer] = (entry.Count + 1, entry.Count == 0 ? priority1 : entry.Priority1);
            if (entry.Count == 0)
            {
                logger.LogInformation("PTP clock: tracking peer {Peer} (announce p1={P1})", peer, priority1);
            }
        }
    }

    public void RemovePeer(IPAddress address)
    {
        var peer = Normalize(address);
        lock (_gate)
        {
            if (_peers.TryGetValue(peer, out var entry))
            {
                if (entry.Count <= 1)
                {
                    _peers.Remove(peer);
                    logger.LogInformation("PTP clock: dropped peer {Peer}", peer);
                }
                else
                {
                    _peers[peer] = (entry.Count - 1, entry.Priority1);
                }
            }
        }
    }

    /// <summary>v4-mapped v6 → plain v4 so the peer set doesn't hold the same host twice.</summary>
    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private (IPAddress Address, byte Priority1)[] PeersSnapshot()
    {
        lock (_gate)
        {
            return [.. _peers.Select(kv => (kv.Key, kv.Value.Priority1))];
        }
    }

    /// <summary>
    /// Grandmaster serve loop, TV cadence: Announce+Signaling every 8th tick
    /// (~1 s), Sync+Follow_Up every 125 ms starting a few ticks in (the TV
    /// announces ~0.4 s before its first Sync — win BMCA first, then serve).
    /// </summary>
    private async Task ServeLoopAsync(CancellationToken ct)
    {
        ushort syncSeq = 0, announceSeq = 0, signalingSeq = 0;
        var tick = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(125));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var peers = PeersSnapshot();
                if (peers.Length == 0)
                {
                    tick = 0;
                    continue;
                }

                var announce = tick % 8 == 0;
                var sync = tick++ >= 3;
                if (announce)
                {
                    announceSeq++;
                    signalingSeq++;
                }

                if (sync)
                {
                    syncSeq++;
                }

                foreach (var (peer, priority1) in peers)
                {
                    try
                    {
                        var generalDest = new IPEndPoint(peer, PtpWire.GeneralPort);
                        if (announce)
                        {
                            await _mux!.SendGeneralAsync(PtpWire.BuildGmAnnounce(announceSeq, ClockId, priority1), generalDest, ct).ConfigureAwait(false);
                            await _mux.SendGeneralAsync(PtpWire.BuildGmSignaling(signalingSeq, ClockId), generalDest, ct).ConfigureAwait(false);
                        }

                        if (sync)
                        {
                            await _mux!.SendEventAsync(PtpWire.BuildGmSync(syncSeq, ClockId),
                                new IPEndPoint(peer, PtpWire.EventPort), ct).ConfigureAwait(false);
                            var now = NowNanos;
                            await _mux.SendGeneralAsync(
                                PtpWire.BuildGmFollowUp(syncSeq, ClockId, (ulong)(now / 1_000_000_000L), (uint)(now % 1_000_000_000L)),
                                generalDest, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "PTP clock: send to {Peer} failed", peer);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Event port: answer any Delay_Req with a Delay_Resp stamped at receive time.</summary>
    private async Task EventRxLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var packet in _consumer!.Event.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var type = PtpWire.ParseType(packet.Data);
                if (_rxLog++ < 12)
                {
                    logger.LogInformation("PTP clock rx: {Type} from {From} ({Len} B)", type, packet.From, packet.Data.Length);
                }

                if (type != PtpMessageType.DelayReq || packet.Data.Length < 34)
                {
                    continue;
                }

                // The success tell: a Delay_Req means the sender has accepted our
                // clock as grandmaster and is measuring path delay against it.
                if (_delayReqLog++ < 8)
                {
                    logger.LogInformation("PTP clock: sender {From} is slaving to our clock (Delay_Req)", packet.From);
                }

                var resp = PtpWire.BuildGmDelayResp(packet.Data, ClockId,
                    (ulong)(packet.ReceivedNanos / 1_000_000_000L), (uint)(packet.ReceivedNanos % 1_000_000_000L));
                try
                {
                    await _mux!.SendGeneralAsync(resp, new IPEndPoint(packet.From, PtpWire.GeneralPort), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "PTP clock: Delay_Resp send failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>General port: nothing to act on in passive mode, but drain and log early traffic.</summary>
    private async Task GeneralRxLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var packet in _consumer!.General.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_rxLog++ < 12)
                {
                    logger.LogInformation("PTP clock rx: {Type} from {From} ({Len} B)",
                        PtpWire.ParseType(packet.Data), packet.From, packet.Data.Length);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
