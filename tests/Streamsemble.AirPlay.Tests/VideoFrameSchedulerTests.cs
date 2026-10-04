using System.Threading.Channels;
using Streamsemble.AirPlay.Sender.Video;
using Streamsemble.Core.Video;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class VideoFrameSchedulerTests
{
    private const long Second = 1_000_000_000;
    private const long Shift = 1_500_000_000;

    [Fact]
    public async Task AFreshOpeningKeyframeCanBeReleasedWithoutLeavingAFutureDisplayStamp()
    {
        var clock = new ManualClock(10 * Second);
        var scheduler = new VideoFrameScheduler(() => clock.Now, clock.Delay);
        var pending = scheduler.ScheduleAsync(Frame(1, clock.Now, keyframe: true), () => Shift).AsTask();
        var wait = await clock.NextDelayAsync();
        Assert.False(pending.IsCompleted);

        scheduler.SetHardSyncEnabled(false);
        var first = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(first);
        Assert.True(first.Value.FirstPicture);
        Assert.Equal(clock.Now + VideoFrameScheduler.SendLeadNanos, first.Value.Frame.TargetNanos);
        Assert.True(wait.Token.IsCancellationRequested);
        Assert.Equal(0, clock.ActiveDelays);
        Assert.NotNull(await scheduler.ScheduleAsync(Frame(2, clock.Now), () => Shift));
    }

    [Fact]
    public async Task HardSyncHoldsFramesUntilTheGroupDeadlineMinusTheDisplayBuffer()
    {
        var clock = new ManualClock(10 * Second);
        var scheduler = await RunningSchedulerAsync(clock);

        var pending = scheduler.ScheduleAsync(Frame(3, clock.Now), () => Shift).AsTask();
        var wait = await clock.NextDelayAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(1400), wait.Duration);
        Assert.False(pending.IsCompleted);

        wait.Complete();
        var sent = (await pending).GetValueOrDefault().Frame;
        Assert.Equal(11_500_000_000, sent.TargetNanos);
        Assert.Equal(VideoFrameScheduler.SendLeadNanos, sent.TargetNanos - clock.Now);
        Assert.True(scheduler.HardSyncEnabled);
        Assert.Equal(0, clock.ActiveDelays);
    }

    [Fact]
    public async Task DisablingInterruptsTheCurrentWaitAndDrainsEveryReferenceFrame()
    {
        var clock = new ManualClock(10 * Second);
        var scheduler = await RunningSchedulerAsync(clock);
        var original = Frame(3, clock.Now);
        var pending = scheduler.ScheduleAsync(original, () => Shift).AsTask();
        var wait = await clock.NextDelayAsync();

        scheduler.SetHardSyncEnabled(false);
        var released = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(released);
        Assert.True(wait.Token.IsCancellationRequested);
        Assert.Equal(0, clock.ActiveDelays);
        Assert.False(scheduler.HardSyncEnabled);
        Assert.Equal(original.Data, released.Value.Frame.Data);
        Assert.InRange(released.Value.Frame.TargetNanos - clock.Now, 100_000_000, 100_001_000);

        // These are stale P-frames from the deep buffer. None may be dropped
        // or require another IDR; their bytes and order are the decode chain.
        var lastStamp = released.Value.Frame.TargetNanos;
        for (var id = 4; id < 94; id++)
        {
            var source = Frame(id, clock.Now - Second + id * 1_000_000);
            var ready = await scheduler.ScheduleAsync(source, () => Shift);
            Assert.NotNull(ready);
            Assert.Equal(source.Data, ready.Value.Frame.Data);
            Assert.False(ready.Value.Frame.IsKeyframe);
            Assert.True(ready.Value.Frame.TargetNanos > lastStamp);
            lastStamp = ready.Value.Frame.TargetNanos;
        }

        Assert.InRange(lastStamp - clock.Now, 100_000_000, 100_001_000);
        Assert.Equal(0, clock.ActiveDelays);
    }

    [Fact]
    public async Task ReenablingPreservesAStaleBacklogThenRebuildsTheDelay()
    {
        var clock = new ManualClock(10 * Second);
        var scheduler = await RunningSchedulerAsync(clock);
        scheduler.SetHardSyncEnabled(false);
        var live = await scheduler.ScheduleAsync(Frame(3, clock.Now), () => Shift);
        scheduler.SetHardSyncEnabled(true);

        // Rapid re-enable can catch the backlog part way through draining.
        // Its old deadlines do not justify breaking the reference chain.
        var stale = await scheduler.ScheduleAsync(Frame(4, 5 * Second), () => Shift);
        Assert.NotNull(stale);
        Assert.True(stale.Value.Frame.TargetNanos > live!.Value.Frame.TargetNanos);
        Assert.False(stale.Value.Frame.IsKeyframe);

        var pending = scheduler.ScheduleAsync(Frame(5, clock.Now), () => Shift).AsTask();
        var wait = await clock.NextDelayAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(1400), wait.Duration);
        Assert.False(pending.IsCompleted);
        wait.Complete();
        var synced = await pending;
        Assert.NotNull(synced);
        Assert.Equal(11_500_000_000, synced.Value.Frame.TargetNanos);
        Assert.True(synced.Value.CaughtUp);
        Assert.Equal(0, clock.ActiveDelays);
    }

    [Fact]
    public async Task RepeatedTogglesReevaluateTheOriginalFrameWithoutAddingTheShiftTwice()
    {
        var clock = new ManualClock(10 * Second);
        var scheduler = await RunningSchedulerAsync(clock);
        var pending = scheduler.ScheduleAsync(Frame(3, clock.Now), () => Shift).AsTask();
        var firstWait = await clock.NextDelayAsync();

        // Stay enabled but change the generation while the same frame waits.
        // Hold cancellation cleanup so the scheduler cannot observe the
        // intermediate off state before the second toggle reaches it.
        var cancellationBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.CancellationBarrier = cancellationBarrier.Task;
        scheduler.SetHardSyncEnabled(false);
        scheduler.SetHardSyncEnabled(true);
        cancellationBarrier.TrySetResult();
        var secondWait = await clock.NextDelayAsync();
        Assert.True(firstWait.Token.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromMilliseconds(1400), secondWait.Duration);
        Assert.False(pending.IsCompleted);

        scheduler.SetHardSyncEnabled(false);
        var released = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(released);
        Assert.InRange(released.Value.Frame.TargetNanos - clock.Now, 100_000_000, 100_001_000);
        Assert.True(secondWait.Token.IsCancellationRequested);
        Assert.Equal(0, clock.ActiveDelays);
    }

    [Fact]
    public async Task CallerCancellationStopsThePendingFrameAndItsDelay()
    {
        var clock = new ManualClock(10 * Second);
        var scheduler = await RunningSchedulerAsync(clock);
        using var cts = new CancellationTokenSource();
        var pending = scheduler.ScheduleAsync(Frame(3, clock.Now), () => Shift, cts.Token).AsTask();
        var wait = await clock.NextDelayAsync();

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(wait.Token.IsCancellationRequested);
        Assert.Equal(0, clock.ActiveDelays);
    }

    [Fact]
    public async Task ResetWakesAndDiscardsOldFramesWhilePreservingTheSelectedMode()
    {
        var clock = new ManualClock(10 * Second);
        var scheduler = await RunningSchedulerAsync(clock);
        var pending = scheduler.ScheduleAsync(Frame(3, clock.Now), () => Shift).AsTask();
        var wait = await clock.NextDelayAsync();

        scheduler.Reset();
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(wait.Token.IsCancellationRequested);
        Assert.Equal(0, clock.ActiveDelays);
        Assert.Null(scheduler.LastLeadMs);

        scheduler.SetHardSyncEnabled(false);
        scheduler.Reset();
        Assert.False(scheduler.HardSyncEnabled);
        Assert.Null(await scheduler.ScheduleAsync(Frame(4, clock.Now)));
        Assert.NotNull(await scheduler.ScheduleAsync(Frame(5, clock.Now, keyframe: true)));
        Assert.NotNull(await scheduler.ScheduleAsync(Frame(6, clock.Now)));
    }

    [Fact]
    public async Task AnOverdueOpeningKeyframeIsSentWithAValidDisplayTimestamp()
    {
        var clock = new ManualClock(10 * Second);
        var scheduler = new VideoFrameScheduler(() => clock.Now, clock.Delay);
        var original = Frame(1, 5 * Second, keyframe: true);
        var ready = await scheduler.ScheduleAsync(original, () => Shift);

        Assert.NotNull(ready);
        Assert.True(ready.Value.FirstPicture);
        Assert.Equal(original.Data, ready.Value.Frame.Data);
        Assert.Equal(clock.Now + VideoFrameScheduler.SendLeadNanos, ready.Value.Frame.TargetNanos);
        Assert.Equal(0, clock.ActiveDelays);
        Assert.NotNull(await scheduler.ScheduleAsync(Frame(2, 5 * Second + 33_000_000), () => Shift));
    }

    [Fact]
    public async Task AStallPreservesThousandsOfReferenceFramesThenResumesHardSyncWithoutANewIdr()
    {
        var clock = new ManualClock(10 * Second);
        var scheduler = await RunningSchedulerAsync(clock);

        // The next P-frame initially has its normal 100 ms presentation lead.
        // A 500 ms stall makes it late after the stream already caught up;
        // this used to drop it and latch every subsequent P-frame out forever.
        var nextSourceStamp = 8_633_000_000L;
        clock.Advance(TimeSpan.FromMilliseconds(500));
        long lastStamp = 0;
        for (var id = 3; id < 3003; id++)
        {
            var original = Frame(id, nextSourceStamp);
            var ready = await scheduler.ScheduleAsync(original, () => Shift);
            Assert.NotNull(ready);
            Assert.False(ready.Value.Frame.IsKeyframe);
            Assert.Equal(original.Data, ready.Value.Frame.Data);
            Assert.Equal(clock.Now + VideoFrameScheduler.SendLeadNanos, ready.Value.Frame.TargetNanos);
            Assert.True(ready.Value.Frame.TargetNanos > lastStamp);
            lastStamp = ready.Value.Frame.TargetNanos;
            nextSourceStamp += 33_000_000;
            clock.Advance(TimeSpan.FromMilliseconds(33));
        }

        Assert.Equal(0, clock.ActiveDelays);
        Assert.True(scheduler.HardSyncEnabled);
        Assert.InRange(scheduler.LastLeadMs!.Value, -368, -366);

        // Once a live deadline leads again, the same uninterrupted reference
        // chain returns to group pacing. No toggle, resize, or IDR is needed.
        var recoveredTarget = clock.Now + Shift;
        var pending = scheduler.ScheduleAsync(Frame(3003, clock.Now), () => Shift).AsTask();
        var wait = await clock.NextDelayAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(1400), wait.Duration);
        Assert.False(pending.IsCompleted);
        wait.Complete();
        var recovered = await pending;
        Assert.NotNull(recovered);
        Assert.True(recovered.Value.CaughtUp);
        Assert.False(recovered.Value.Frame.IsKeyframe);
        Assert.Equal(recoveredTarget, recovered.Value.Frame.TargetNanos);
        Assert.Equal(VideoFrameScheduler.SendLeadNanos, recovered.Value.Frame.TargetNanos - clock.Now);
        Assert.Equal(0, clock.ActiveDelays);

        // A later mode switch also retains the chain recovered from that stall.
        scheduler.SetHardSyncEnabled(false);
        Assert.NotNull(await scheduler.ScheduleAsync(Frame(3004, clock.Now), () => Shift));
    }

    [Fact]
    public async Task CodecReconfigurationStillRequiresAKeyframeInEitherMode()
    {
        var clock = new ManualClock(10 * Second);
        var scheduler = await RunningSchedulerAsync(clock);
        scheduler.SetHardSyncEnabled(false);
        scheduler.AwaitKeyframe();
        Assert.Null(await scheduler.ScheduleAsync(Frame(3, clock.Now)));
        Assert.NotNull(await scheduler.ScheduleAsync(Frame(4, clock.Now, keyframe: true)));
        Assert.NotNull(await scheduler.ScheduleAsync(Frame(5, clock.Now)));
    }

    private static async Task<VideoFrameScheduler> RunningSchedulerAsync(ManualClock clock)
    {
        var scheduler = new VideoFrameScheduler(() => clock.Now, clock.Delay);
        // Connection startup has delayed the opening IDR; then the backlog
        // catches up. The next test frame exercises ordinary live scheduling.
        Assert.NotNull(await scheduler.ScheduleAsync(Frame(1, 8 * Second, keyframe: true), () => Shift));
        var live = await scheduler.ScheduleAsync(Frame(2, 8_600_000_000), () => Shift);
        Assert.NotNull(live);
        Assert.True(live.Value.CaughtUp);
        return scheduler;
    }

    private static VideoFrame Frame(int id, long stamp, bool keyframe = false)
        => new(new byte[] { 0, 0, 0, 2, keyframe ? (byte)0x65 : (byte)0x41, (byte)id }, stamp, keyframe);

    private sealed class ManualClock(long nowNanos)
    {
        private readonly Channel<DelayRequest> _waits = Channel.CreateUnbounded<DelayRequest>();
        private long _nowNanos = nowNanos;
        private int _activeDelays;

        public long Now => Interlocked.Read(ref _nowNanos);
        public int ActiveDelays => Volatile.Read(ref _activeDelays);
        public Task CancellationBarrier { get; set; } = Task.CompletedTask;

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _nowNanos, duration.Ticks * 100);

        public async Task Delay(TimeSpan duration, CancellationToken ct)
        {
            var request = new DelayRequest(duration, ct, () => Interlocked.Add(ref _nowNanos, duration.Ticks * 100));
            Interlocked.Increment(ref _activeDelays);
            _waits.Writer.TryWrite(request);
            try
            {
                await request.Completion.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await CancellationBarrier;
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _activeDelays);
            }
        }

        public Task<DelayRequest> NextDelayAsync()
            => _waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class DelayRequest(TimeSpan duration, CancellationToken token, Action advance)
    {
        public TimeSpan Duration => duration;
        public CancellationToken Token => token;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete()
        {
            advance();
            Completion.TrySetResult();
        }
    }
}
