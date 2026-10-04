using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.AirPlay.Receiver.Audio;
using Streamsemble.Core.Audio;
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
            (pcm, _) => { lock (emitted) { emitted.Add(pcm.Span[0]); } },
            NullLogger.Instance,
            clockNanos: () => Volatile.Read(ref clock));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = scheduler.RunAsync(cts.Token);

        // Consecutive frames render 10 fake-minutes out, then ~8 ms later.
        scheduler.SetAnchor(44100, clock + 600_000_000_000);
        scheduler.Enqueue(44100, Frame(1));
        scheduler.Enqueue(44100 + PcmFrame.SamplesPerFrame, Frame(2));
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

        Volatile.Write(ref clock, Volatile.Read(ref clock) + 8_000_000);
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
            (_, _) => Interlocked.Increment(ref emitted),
            NullLogger.Instance,
            clockNanos: () => Volatile.Read(ref clock));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = scheduler.RunAsync(cts.Token);

        scheduler.Enqueue(0, Frame(1));
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
            (_, _) => Interlocked.Increment(ref emitted),
            NullLogger.Instance,
            leadNanos: 1_500_000_000,
            clockNanos: () => Volatile.Read(ref clock));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = scheduler.RunAsync(cts.Token);

        // Audible 10 fake-minutes out; with a 1.5 s lead the emit time is
        // 1.5 s before that.
        var audibleAt = clock + 600_000_000_000;
        scheduler.SetAnchor(44100, audibleAt);
        scheduler.Enqueue(44100, Frame(1));
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

    [Fact]
    public async Task RepeatedPacketLossPreservesTwentyMinutesOfRenderTimeline()
    {
        const long anchorNanos = 5_000_000_000_000;
        const int frameCount = 44100 * 60 * 20 / PcmFrame.SamplesPerFrame;
        long emittedSamples = 0;
        var missingFrames = 0;
        var concealedFrames = 0;
        var mismatchedTargets = 0;
        var scheduler = new AnchoredPcmScheduler(
            (pcm, target) =>
            {
                // This is the same contiguous sample counter the receiver
                // source and outgoing AAC encoder use. Omitting a loss must
                // never make that counter disagree with the render stamps.
                var expected = anchorNanos + emittedSamples * 1_000_000_000L / 44100;
                if (target != expected)
                {
                    mismatchedTargets++;
                }

                if (pcm.Span[0] == 0)
                {
                    concealedFrames++;
                }

                emittedSamples += pcm.Length / AudioFormat.Canonical.BlockAlign;
            },
            NullLogger.Instance,
            clockNanos: () => anchorNanos + 1_201_000_000_000);
        scheduler.SetAnchor(0, anchorNanos);
        var audio = Frame(7);
        for (var i = 0; i < frameCount; i++)
        {
            if (i > 0 && i % 881 == 0 && i < frameCount - 1)
            {
                missingFrames++;
                continue;
            }

            scheduler.Enqueue((uint)(i * PcmFrame.SamplesPerFrame), audio);
        }

        await DrainAsync(scheduler);

        Assert.True(missingFrames * PcmFrame.SamplesPerFrame > 44100); // >1 s of cumulative lead loss before the fix
        Assert.Equal(missingFrames, concealedFrames);
        Assert.Equal((long)frameCount * PcmFrame.SamplesPerFrame, emittedSamples);
        Assert.Equal(0, mismatchedTargets);
    }

    [Fact]
    public async Task ConcealsGapAcrossRtpWrapAndIgnoresLateDuplicate()
    {
        const long anchorNanos = 5_000_000_000_000;
        var firstRtp = uint.MaxValue - 351;
        var emitted = new List<(byte[] Pcm, long Target)>();
        var scheduler = new AnchoredPcmScheduler(
            (pcm, target) => emitted.Add((pcm.ToArray(), target)),
            NullLogger.Instance,
            clockNanos: () => anchorNanos + 1_000_000_000);
        scheduler.SetAnchor(firstRtp, anchorNanos);
        scheduler.Enqueue(firstRtp, Frame(1));
        scheduler.Enqueue(352, Frame(2)); // RTP 0 was lost
        scheduler.Enqueue(0, Frame(3)); // late arrival of the concealed frame
        scheduler.Enqueue(352, Frame(2)); // duplicate
        scheduler.Enqueue(704, Frame(4));

        await DrainAsync(scheduler);

        Assert.Equal(new byte[] { 1, 0, 2, 4 }, emitted.Select(frame => frame.Pcm[0]));
        Assert.All(emitted[1].Pcm, sample => Assert.Equal((byte)0, sample));
        for (var i = 0; i < emitted.Count; i++)
        {
            Assert.Equal(anchorNanos + i * PcmFrame.SamplesPerFrame * 1_000_000_000L / 44100, emitted[i].Target);
        }
    }

    [Fact]
    public async Task TrimsOverlapWithoutRepeatingAlreadyScheduledSamples()
    {
        const long anchorNanos = 5_000_000_000_000;
        var emitted = new List<(int Samples, long Target)>();
        var scheduler = new AnchoredPcmScheduler(
            (pcm, target) => emitted.Add((pcm.Length / AudioFormat.Canonical.BlockAlign, target)),
            NullLogger.Instance,
            clockNanos: () => anchorNanos + 1_000_000_000);
        scheduler.SetAnchor(0, anchorNanos);
        scheduler.Enqueue(0, Frame(1));
        scheduler.Enqueue(176, Frame(2));

        await DrainAsync(scheduler);

        Assert.Equal(new[] { 352, 176 }, emitted.Select(frame => frame.Samples));
        Assert.Equal(anchorNanos + 352 * 1_000_000_000L / 44100, emitted[1].Target);
    }

    [Fact]
    public async Task FlushResetsGapTracking()
    {
        const long anchorNanos = 5_000_000_000_000;
        var emitted = new List<byte>();
        var scheduler = new AnchoredPcmScheduler(
            (pcm, _) => emitted.Add(pcm.Span[0]),
            NullLogger.Instance,
            clockNanos: () => anchorNanos + 1_000_000_000);
        scheduler.SetAnchor(0, anchorNanos);
        scheduler.Enqueue(0, Frame(1));
        scheduler.Flush();
        scheduler.Enqueue(704, Frame(2));

        await DrainAsync(scheduler);

        Assert.Equal(new byte[] { 2 }, emitted);
    }

    [Fact]
    public async Task FlushDiscardsTheFrameAlreadyWaitingForItsDeadline()
    {
        const long anchorNanos = 5_000_000_000_000;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var emitted = new List<byte>();
        var scheduler = new AnchoredPcmScheduler(
            (pcm, _) =>
            {
                lock (emitted) { emitted.Add(pcm.Span[0]); }
                if (pcm.Span[0] == 2) fresh.TrySetResult();
            },
            NullLogger.Instance,
            clockNanos: () => { waiting.TrySetResult(); return anchorNanos; });
        scheduler.SetAnchor(0, anchorNanos + 10_000_000_000);
        scheduler.Enqueue(0, Frame(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = scheduler.RunAsync(cts.Token);

        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Flush();
        scheduler.SetAnchor(352, anchorNanos);
        scheduler.Enqueue(352, Frame(2));
        await fresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (emitted) { Assert.Equal(new byte[] { 2 }, emitted); }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task LargeDiscontinuityDoesNotAllocateUnboundedSilence()
    {
        const long anchorNanos = 5_000_000_000_000;
        var emitted = new List<byte>();
        var scheduler = new AnchoredPcmScheduler(
            (pcm, _) => emitted.Add(pcm.Span[0]),
            NullLogger.Instance,
            clockNanos: () => anchorNanos + 3_601_000_000_000);
        scheduler.SetAnchor(0, anchorNanos);
        scheduler.Enqueue(0, Frame(1));
        scheduler.Enqueue(44100 * 3600, Frame(2));

        await DrainAsync(scheduler);

        Assert.Equal(new byte[] { 1, 2 }, emitted);
    }

    private static byte[] Frame(byte marker)
    {
        var frame = new byte[PcmFrame.CanonicalFrameBytes];
        Array.Fill(frame, marker);
        return frame;
    }

    private static async Task DrainAsync(AnchoredPcmScheduler scheduler)
    {
        // Every queued target in these tests is already due, so RunAsync
        // drains synchronously before waiting for the next channel item.
        using var cts = new CancellationTokenSource();
        var run = scheduler.RunAsync(cts.Token);
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
