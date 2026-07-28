using System.Threading.Channels;
using Claunia.PropertyList;
using Streamsemble.AirPlay.Receiver;
using Streamsemble.AirPlay.Receiver.Audio;
using Streamsemble.Core.Audio;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class AnchorGateTests
{
    /// <summary>
    /// The receiver's parse must invert the sender's secs+frac64 split — a
    /// mistake here silently shifts every inbound sender's lip sync.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(999_999_999L)] // maximal fraction, zero seconds
    [InlineData(1_753_562_000_123_456_789L)]
    [InlineData(1_700_000_000_999_999_999L)]
    public void AnchorNanos_InvertsSenderFracMath(long nanos)
    {
        var fracNanos = (ulong)(nanos % 1_000_000_000);
        var frac64 = (ulong)(((UInt128)fracNanos << 64) / 1_000_000_000);
        var plist = new NSDictionary
        {
            { "rate", new NSNumber(1) },
            { "rtpTime", new NSNumber(1234) },
            { "networkTimeSecs", new NSNumber(nanos / 1_000_000_000) },
            { "networkTimeFrac", new NSNumber(unchecked((long)frac64)) },
        };

        var parsed = ReceiverSession.AnchorNanos(plist);

        Assert.NotNull(parsed);
        Assert.InRange(parsed.Value, nanos - 1, nanos + 1);
    }

    [Fact]
    public void AnchorNanos_NullWithoutNetworkTime()
    {
        var plist = new NSDictionary { { "rate", new NSNumber(1) } };
        Assert.Null(ReceiverSession.AnchorNanos(plist));
    }

    [Fact]
    public async Task Emitter_HoldsEmissionUntilGateClock()
    {
        var clock = 1_000_000_000_000L;
        var channel = Channel.CreateUnbounded<byte[]>();
        var emitted = 0;
        var emitter = new PacedPcmEmitter(
            channel.Reader,
            (_, _) => Interlocked.Increment(ref emitted),
            clockNanos: () => Volatile.Read(ref clock));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = emitter.RunAsync(cts.Token);

        channel.Writer.TryWrite(new byte[PcmFrame.CanonicalFrameBytes]);
        emitter.GoAt(clock + 600_000_000_000); // 10 min ahead on the fake clock
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref emitted));

        Volatile.Write(ref clock, clock + 600_000_000_000);
        var deadline = DateTime.UtcNow.AddSeconds(5); // hold re-checks every ≤250 ms
        while (Volatile.Read(ref emitted) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal(1, Volatile.Read(ref emitted));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Emitter_GoOpensImmediately()
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        var emitted = 0;
        var emitter = new PacedPcmEmitter(
            channel.Reader,
            (_, _) => Interlocked.Increment(ref emitted),
            clockNanos: () => 1_000_000_000_000L);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = emitter.RunAsync(cts.Token);

        channel.Writer.TryWrite(new byte[PcmFrame.CanonicalFrameBytes]);
        emitter.Go();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref emitted) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, Volatile.Read(ref emitted));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
