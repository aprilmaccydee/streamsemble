using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.AirPlay.Receiver.Audio;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class AnchoredPcmSchedulerTests
{
    [Fact]
    public async Task HoldsFramesUntilAnchoredRenderTime()
    {
        var clock = 5_000_000_000_000L;
        var emitted = new List<uint>();
        var scheduler = new AnchoredPcmScheduler(
            pcm => { lock (emitted) { emitted.Add((uint)pcm.Length); } },
            NullLogger.Instance,
            clockNanos: () => Volatile.Read(ref clock));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = scheduler.RunAsync(cts.Token);

        // Frame 44100 renders 10 fake-minutes out; frame 88200 a second later.
        scheduler.SetAnchor(44100, clock + 600_000_000_000);
        scheduler.Enqueue(44100, new byte[1]);
        scheduler.Enqueue(88200, new byte[2]);
        await Task.Delay(400);
        lock (emitted)
        {
            Assert.Empty(emitted);
        }

        // Jump the clock past the first frame's time but not the second's.
        Volatile.Write(ref clock, clock + 600_000_000_000);
        await WaitForCountAsync(emitted, 1);
        lock (emitted)
        {
            Assert.Equal([1u], emitted);
        }

        Volatile.Write(ref clock, Volatile.Read(ref clock) + 1_000_000_000);
        await WaitForCountAsync(emitted, 2);
        lock (emitted)
        {
            Assert.Equal([1u, 2u], emitted);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task FallsBackToArrivalWhenNoAnchorAppears()
    {
        var clockStart = 5_000_000_000_000L;
        var clock = clockStart;
        var emitted = 0;
        var scheduler = new AnchoredPcmScheduler(
            _ => Interlocked.Increment(ref emitted),
            NullLogger.Instance,
            clockNanos: () => Volatile.Read(ref clock));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = scheduler.RunAsync(cts.Token);

        scheduler.Enqueue(0, new byte[1]);
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref emitted));

        // Fake clock passes the 1 s anchor-wait budget: frame goes out unanchored.
        Volatile.Write(ref clock, clockStart + 1_100_000_000);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref emitted) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal(1, Volatile.Read(ref emitted));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task EmitsLeadNanosBeforeTheAudibleTime()
    {
        var clock = 5_000_000_000_000L;
        var emitted = 0;
        var scheduler = new AnchoredPcmScheduler(
            _ => Interlocked.Increment(ref emitted),
            NullLogger.Instance,
            leadNanos: 1_500_000_000,
            clockNanos: () => Volatile.Read(ref clock));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = scheduler.RunAsync(cts.Token);

        // Audible 10 fake-minutes out; with a 1.5 s lead the emit time is
        // 1.5 s before that.
        var audibleAt = clock + 600_000_000_000;
        scheduler.SetAnchor(44100, audibleAt);
        scheduler.Enqueue(44100, new byte[1]);
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref emitted));

        // Just before the emit point: still held.
        Volatile.Write(ref clock, audibleAt - 1_600_000_000);
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref emitted));

        // At the emit point (audible − lead): released, well before audibleAt.
        Volatile.Write(ref clock, audibleAt - 1_500_000_000);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref emitted) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal(1, Volatile.Read(ref emitted));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private static async Task WaitForCountAsync(List<uint> emitted, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (emitted)
            {
                if (emitted.Count >= count)
                {
                    return;
                }
            }

            await Task.Delay(25);
        }
    }
}
