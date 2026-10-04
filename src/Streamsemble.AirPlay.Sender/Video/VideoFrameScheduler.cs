using Streamsemble.Core.Video;
using Streamsemble.Timing.Ptp;

namespace Streamsemble.AirPlay.Sender.Video;

/// <summary>
/// Keeps the deep video buffer in the hub while allowing a live switch to a
/// responsive picture. Queued H.264 frames must always retain their reference
/// chain when switching: a Mac may not send another keyframe this session.
/// </summary>
internal sealed class VideoFrameScheduler
{
    // SETUP declares a 100 ms display buffer. Mirror receivers cannot hold
    // the speaker group's much longer delay reliably; that deep buffer must
    // stay in the hub's source queue until this close to the render deadline.
    internal const long SendLeadNanos = 100_000_000;
    private const long MaxLatenessNanos = 250_000_000;
    private const long WaitToleranceNanos = 2_000_000;

    private readonly object _gate = new();
    private readonly Func<long> _nowNanos;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private ModeState _mode = new(true, 0);
    private long _streamGeneration;
    private long _appliedModeVersion;
    private long _lastTargetNanos;
    private bool _started;
    private bool _awaitingKeyframe = true;
    private bool _catchingUp;
    private double? _lastLeadMs;

    public VideoFrameScheduler(
        Func<long>? nowNanos = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _nowNanos = nowNanos ?? (() => PtpReceiverClock.NowNanos);
        _delay = delay ?? Task.Delay;
    }

    public bool HardSyncEnabled
    {
        get { lock (_gate) { return _mode.Enabled; } }
    }

    public double? LastLeadMs
    {
        get { lock (_gate) { return _lastLeadMs; } }
    }

    public void SetHardSyncEnabled(bool enabled)
    {
        ModeState previous;
        lock (_gate)
        {
            if (_mode.Enabled == enabled)
            {
                return;
            }

            previous = _mode;
            _mode = new ModeState(enabled, previous.Version + 1);
        }

        previous.Changed.TrySetResult();
    }

    /// <summary>Starts a fresh decoder timeline without changing the user's mode.</summary>
    public void Reset()
    {
        ModeState previous;
        lock (_gate)
        {
            _streamGeneration++;
            _started = false;
            _awaitingKeyframe = true;
            _catchingUp = false;
            _lastTargetNanos = 0;
            _lastLeadMs = null;
            previous = _mode;
            _mode = new ModeState(previous.Enabled, previous.Version + 1);
            _appliedModeVersion = _mode.Version;
        }

        previous.Changed.TrySetResult();
    }

    public void AwaitKeyframe()
    {
        lock (_gate) { _awaitingKeyframe = true; }
    }

    /// <summary>
    /// Returns null only when the decoder cannot use this frame, an ordinary
    /// hard-sync deadline was missed, or the stream ended during the wait.
    /// Mode changes re-evaluate the original frame, never an already shifted
    /// or restamped copy.
    /// </summary>
    public async ValueTask<ScheduledVideoFrame?> ScheduleAsync(
        VideoFrame sourceFrame,
        Func<long>? audioTimelineShiftNanos = null,
        CancellationToken ct = default)
    {
        long streamGeneration;
        lock (_gate) { streamGeneration = _streamGeneration; }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ModeState mode;
            long waitNanos;
            lock (_gate)
            {
                if (streamGeneration != _streamGeneration)
                {
                    return null;
                }

                mode = _mode;
                if (_appliedModeVersion != mode.Version)
                {
                    _appliedModeVersion = mode.Version;
                    // Re-enabling while the low-latency backlog is still
                    // draining must not drop its remaining reference frames.
                    _catchingUp = _started;
                }

                if (_awaitingKeyframe && !sourceFrame.IsKeyframe)
                {
                    return null;
                }

                var now = _nowNanos();
                var stampedHardSync = mode.Enabled && sourceFrame.TargetNanos > 0;
                var targetNanos = stampedHardSync
                    ? sourceFrame.TargetNanos + Math.Max(0, audioTimelineShiftNanos?.Invoke() ?? 0)
                    : now + SendLeadNanos;
                var leadNanos = targetNanos - now;
                _lastLeadMs = leadNanos / 1e6;
                var firstPicture = !_started;
                var caughtUp = false;

                if (!firstPicture && stampedHardSync)
                {
                    if (leadNanos < -MaxLatenessNanos && !_catchingUp)
                    {
                        _awaitingKeyframe = true;
                        return null;
                    }

                    caughtUp = _catchingUp && leadNanos >= -MaxLatenessNanos;
                }

                // The last hard-sync frame is already about 100 ms ahead at
                // the display. Do not move its clock backwards when draining
                // the queue at wire speed, including very rapid toggles.
                targetNanos = Math.Max(targetNanos, _lastTargetNanos + 1);
                // An old opening IDR primes the decoder immediately despite
                // its lateness. A fresh one still waits for its deadline, so
                // it cannot strand an excessive future stamp at the display.
                waitNanos = stampedHardSync
                    ? targetNanos - SendLeadNanos - now
                    : 0;

                if (waitNanos <= WaitToleranceNanos)
                {
                    _started = true;
                    _awaitingKeyframe = false;
                    _catchingUp = firstPicture || (_catchingUp && !caughtUp);
                    _lastTargetNanos = targetNanos;
                    return new ScheduledVideoFrame(
                        sourceFrame with { TargetNanos = targetNanos }, firstPicture, caughtUp, leadNanos / 1e6);
                }
            }

            await WaitForDeadlineOrModeChangeAsync(waitNanos, mode.Changed.Task, ct).ConfigureAwait(false);
        }
    }

    private async Task WaitForDeadlineOrModeChangeAsync(long waitNanos, Task modeChanged, CancellationToken ct)
    {
        // Each wait owns and observes its delay, so toggles do not leave long
        // timers or cancellation registrations alive behind the video pump.
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = _delay(TimeSpan.FromTicks(waitNanos / 100), waitCts.Token);
        if (await Task.WhenAny(delay, modeChanged).ConfigureAwait(false) != delay)
        {
            waitCts.Cancel();
        }

        try
        {
            await delay.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && waitCts.IsCancellationRequested)
        {
            // A mode change or stream reset wakes the scheduler to re-check.
        }

        ct.ThrowIfCancellationRequested();
    }

    private sealed record ModeState(bool Enabled, long Version)
    {
        public TaskCompletionSource Changed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal readonly record struct ScheduledVideoFrame(
    VideoFrame Frame,
    bool FirstPicture,
    bool CaughtUp,
    double LeadMs);
