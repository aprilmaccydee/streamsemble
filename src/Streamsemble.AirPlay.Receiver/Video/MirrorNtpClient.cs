using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Streamsemble.Timing.Ptp;

namespace Streamsemble.AirPlay.Receiver.Video;

/// <summary>
/// Mirror timing is receiver-pulled: the sender opens a UDP port, advertises it
/// as <c>timingPort</c> in the stream SETUP, and waits for us to ask. Every
/// video packet's presentation stamp is on that clock, so without this the
/// stamps are unusable numbers.
///
/// The exchange is the same 32-byte format
/// <see cref="Streamsemble.Timing.NtpTimingResponder"/> answers with the roles
/// swapped, and the arithmetic is textbook NTP: with our transmit t1, their
/// receive t2, their transmit t3 and our receive t4, the offset is
/// ((t2−t1) + (t3−t4))/2 and the round trip is (t4−t1) − (t3−t2). Unlike the
/// audio path's one-way min-filter — which has only arrival times to work with
/// and so measures offset-plus-path-delay — a round trip cancels symmetric
/// delay outright, so we keep the sample with the SMALLEST round trip in the
/// window rather than the smallest offset.
/// </summary>
public sealed class MirrorNtpClient(IPEndPoint senderTiming, ILogger logger) : IDisposable
{
    /// <summary>How often to re-measure. Senders poll at about this rate too.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Samples kept; at the poll interval this is about a minute of history.</summary>
    private const int Window = 20;

    private readonly (long Offset, long RoundTrip)[] _samples = new (long, long)[Window];
    private readonly UdpClient _socket = new(AddressFamily.InterNetworkV6);
    private CancellationTokenSource? _cts;
    private int _count;
    private int _next;
    private ushort _sequence;
    private long _offsetNanos;
    private bool _locked;

    /// <summary>True once at least one exchange completed — stamps are meaningless before that.</summary>
    public bool IsLocked => _locked;

    /// <summary>The port we speak from, which is what the session SETUP reply reports as our timingPort.</summary>
    public int LocalPort { get; private set; }

    /// <summary>
    /// Best (sender − local) offset in nanoseconds from the current window.
    /// This is NTP's θ, which is defined as the amount to ADD TO THE LOCAL
    /// clock to get the sender's — so converting the other way, which is all we
    /// ever do, SUBTRACTS it. Getting that backwards does not fail loudly: the
    /// stamps stay well-formed and land billions of seconds from now.
    /// </summary>
    public long OffsetNanos => Volatile.Read(ref _offsetNanos);

    /// <summary>Round trip of the sample the offset came from, for the telemetry panel.</summary>
    public long BestRoundTripNanos { get; private set; }

    /// <summary>A sender-clock instant on our grandmaster timeline.</summary>
    public long ToLocalNanos(long senderNanos) => senderNanos - OffsetNanos;

    public void Start()
    {
        _socket.Client.DualMode = true;
        _socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        LocalPort = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // Prime hard and fast: the first video packets arrive within
        // milliseconds of the stream opening and cannot be stamped until an
        // exchange has completed, so burst four before settling into the
        // steady-state poll.
        try
        {
            for (var i = 0; i < 4 && !ct.IsCancellationRequested; i++)
            {
                await ExchangeAsync(ct).ConfigureAwait(false);
                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await ExchangeAsync(ct).ConfigureAwait(false);
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
            logger.LogWarning(ex, "mirror timing client stopped");
        }
    }

    private async Task ExchangeAsync(CancellationToken ct)
    {
        var request = new byte[32];
        request[0] = 0x80;
        request[1] = 0xD2; // 0x52 request | marker
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), _sequence++);

        var t1 = PtpReceiverClock.NowNanos;
        BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(24), ToNtp(t1));

        try
        {
            await _socket.SendAsync(request, senderTiming, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            var result = await _socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            var t4 = PtpReceiverClock.NowNanos;

            var reply = result.Buffer;
            if (reply.Length < 32 || (reply[1] & 0x7F) != 0x53)
            {
                return;
            }

            var t1Echo = FromNtp(BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(8)));
            var t2 = FromNtp(BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(16)));
            var t3 = FromNtp(BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(24)));

            // A reply carrying someone else's origin timestamp belongs to an
            // earlier, timed-out request; pairing it with this t4 would invent
            // a round trip out of the gap between them.
            if (Math.Abs(t1Echo - t1) > 1_000_000)
            {
                return;
            }

            Record(offset: ((t2 - t1) + (t3 - t4)) / 2, roundTrip: (t4 - t1) - (t3 - t2));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // One dropped exchange; the next poll covers it.
        }
    }

    private void Record(long offset, long roundTrip)
    {
        _samples[_next] = (offset, roundTrip);
        _next = (_next + 1) % _samples.Length;
        _count = Math.Min(_count + 1, _samples.Length);

        var best = _samples[0];
        for (var i = 1; i < _count; i++)
        {
            if (_samples[i].RoundTrip < best.RoundTrip)
            {
                best = _samples[i];
            }
        }

        Volatile.Write(ref _offsetNanos, best.Offset);
        BestRoundTripNanos = best.RoundTrip;

        if (!_locked)
        {
            _locked = true;
            logger.LogInformation(
                "mirror timing locked to sender {Peer}: sender is {OffsetS:F3} s from our clock, round trip {Rtt:F2} ms",
                senderTiming, best.Offset / 1e9, best.RoundTrip / 1e6);
        }
    }

    private const long NtpToUnixSeconds = 2_208_988_800L;

    private static ulong ToNtp(long unixNanos)
    {
        var seconds = unixNanos / 1_000_000_000L;
        var nanos = unixNanos % 1_000_000_000L;
        var fraction = (ulong)(((UInt128)nanos << 32) / 1_000_000_000);
        return ((ulong)(seconds + NtpToUnixSeconds) << 32) | (uint)fraction;
    }

    private static long FromNtp(ulong ntp)
    {
        var seconds = (long)(ntp >> 32) - NtpToUnixSeconds;
        var fractionNanos = (long)(((ulong)(uint)ntp * 1_000_000_000UL) >> 32);
        return seconds * 1_000_000_000L + fractionNanos;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _socket.Dispose();
        _cts?.Dispose();
    }
}
