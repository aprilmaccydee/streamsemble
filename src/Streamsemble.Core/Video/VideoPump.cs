using Microsoft.Extensions.Logging;

namespace Streamsemble.Core.Video;

/// <summary>
/// Binds the video source to the video sink for the process lifetime. Simpler
/// than <see cref="Audio.AudioPump"/> because there is nothing to arbitrate:
/// one screen mirrors at a time, so the pump only has to start the sink when a
/// sender appears, forward frames, and stop it when the sender goes away.
///
/// The sink cannot open a stream until the parameter sets are known, so the
/// pump holds frames back until the first codec config arrives — in practice a
/// handful of milliseconds, since senders lead with it.
/// </summary>
public sealed class VideoPump(IVideoSource source, IVideoSink sink, ILogger<VideoPump> logger) : IAsyncDisposable
{
    private const int MaxStartupBacklogFrames = 256;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _sinkGate = new(1, 1);
    private Task _loop = Task.CompletedTask;
    private Task _pendingStop = Task.CompletedTask;
    private long _streamGeneration;
    private VideoCodecConfig? _streamConfig;

    public void Start(CancellationToken ct)
    {
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
        source.ActiveChanged += OnActiveChanged;
        _loop = RunAsync(_linked.Token);
    }

    private CancellationTokenSource? _linked;

    private void OnActiveChanged(object? sender, bool active)
    {
        if (active)
        {
            return;
        }

        // Stopping is fire-and-forget from an event handler; the sink's own
        // teardown logs anything that goes wrong.
        _ = StopSinkAsync();
    }

    private Task StopSinkAsync()
    {
        Task previousStop;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateGate)
        {
            // Invalidate synchronously, before teardown waits for an in-flight
            // connect/frame. Old replay must never publish its config again.
            _streamGeneration++;
            _streamConfig = null;
            previousStop = _pendingStop;
            _pendingStop = completion.Task;
        }

        _ = FinishStopAsync(previousStop, completion);
        return completion.Task;
    }

    private async Task FinishStopAsync(Task previousStop, TaskCompletionSource completion)
    {
        try
        {
            await previousStop.ConfigureAwait(false);
            await _sinkGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await sink.StopStreamAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _sinkGate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "video sink teardown failed");
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in source.Frames.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var config = frame.CodecConfig ?? source.CodecConfig;
                if (config is null || !source.IsActive)
                {
                    // Nothing downstream can decode this yet.
                    continue;
                }

                long generation;
                bool needsStart;
                lock (_stateGate)
                {
                    generation = _streamGeneration;
                    needsStart = _streamConfig is null;
                }

                if (needsStart)
                {
                    if (!await StartSinkAsync(config, generation, ct).ConfigureAwait(false))
                    {
                        continue;
                    }

                    // Opening the output takes seconds (pairing, SETUP, a TCP
                    // connect), and the source has been filling its queue the
                    // whole time — the frame in hand is the oldest of that
                    // backlog. History, but not droppable history: the sender
                    // opened the stream with its one IDR and will not send
                    // another until its config changes, so the backlog holds
                    // the only entry point there is. Replay from the most
                    // recent keyframe — the chain from there is unbroken, so
                    // it decodes cleanly and the sink fast-forwards it to the
                    // live edge. Frames BEFORE that keyframe are the ones a
                    // display would render as corruption (their references are
                    // gone); those are the ones to drop.
                    List<VideoFrame> backlog;
                    lock (_stateGate)
                    {
                        if (generation != _streamGeneration || !source.IsActive)
                        {
                            continue;
                        }

                        backlog = [frame];
                        // A read releases a blocked TCP producer. Bound this
                        // snapshot so that producer cannot refill the channel
                        // into an ever-growing List while we drain it.
                        for (var i = 0; i < MaxStartupBacklogFrames && source.Frames.TryRead(out var queued); i++)
                        {
                            backlog.Add(queued);
                        }
                    }

                    var entry = backlog.FindLastIndex(f => f.IsKeyframe);
                    if (entry < 0)
                    {
                        // We joined without an opening IDR; nothing held now
                        // would decode. Wait for the source's next keyframe.
                        logger.LogWarning(
                            "dropped {Count} backlogged frames — no keyframe among them, waiting for the source to send one",
                            backlog.Count);
                        continue;
                    }

                    logger.LogInformation(
                        "replaying {Kept} backlogged frames from the last keyframe ({Dropped} older dropped)",
                        backlog.Count - entry, entry);
                    foreach (var replay in backlog.Skip(entry))
                    {
                        if (!await ForwardAsync(replay, generation, ct).ConfigureAwait(false))
                        {
                            break;
                        }
                    }

                    continue;
                }

                await ForwardAsync(frame, generation, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "video pump failed");
        }
    }

    private async Task<bool> StartSinkAsync(VideoCodecConfig config, long generation, CancellationToken ct)
    {
        Task pendingStop;
        lock (_stateGate) { pendingStop = _pendingStop; }
        await pendingStop.WaitAsync(ct).ConfigureAwait(false);
        await _sinkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (generation != _streamGeneration || !source.IsActive)
                {
                    return false;
                }

                _streamConfig = config;
            }

            logger.LogInformation("video stream starting: {Config}", config.Describe());
            await sink.StartStreamAsync(config, ct).ConfigureAwait(false);
            lock (_stateGate) { return generation == _streamGeneration; }
        }
        finally
        {
            _sinkGate.Release();
        }
    }

    private async ValueTask<bool> ForwardAsync(VideoFrame frame, long generation, CancellationToken ct)
    {
        await _sinkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = frame.CodecConfig ?? source.CodecConfig;
            bool reconfigure;
            lock (_stateGate)
            {
                if (generation != _streamGeneration || _streamConfig is null)
                {
                    return false;
                }

                reconfigure = config is not null && !ReferenceEquals(_streamConfig, config);
                if (reconfigure)
                {
                    _streamConfig = config;
                }
            }

            if (reconfigure)
            {
                // A resize can happen while the TV is still connecting. Its
                // config must precede the new IDR, including backlog replay.
                logger.LogInformation("video stream reconfigured: {Config}", config!.Describe());
                await sink.ReconfigureAsync(config, ct).ConfigureAwait(false);
            }

            lock (_stateGate)
            {
                if (generation != _streamGeneration)
                {
                    return false;
                }
            }

            await sink.WriteAsync(frame, ct).ConfigureAwait(false);
            lock (_stateGate) { return generation == _streamGeneration; }
        }
        finally
        {
            _sinkGate.Release();
        }
    }

    /// <summary>
    /// Disposal happens twice in practice — the container owns this and the
    /// hosted service that drives it also tears it down — so it has to be
    /// idempotent rather than throwing on the second pass.
    /// </summary>
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        source.ActiveChanged -= OnActiveChanged;
        _lifetime.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
            // Loop failures are already logged.
        }

        await StopSinkAsync().ConfigureAwait(false);
        _sinkGate.Dispose();
        _linked?.Dispose();
        _lifetime.Dispose();
    }
}
