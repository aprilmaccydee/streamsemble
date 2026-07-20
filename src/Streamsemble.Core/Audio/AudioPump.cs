using Microsoft.Extensions.Logging;
using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Metadata;

namespace Streamsemble.Core.Audio;

/// <summary>
/// Moves frames from the arbiter's live source into the sink, and forwards
/// that source's volume/metadata events. Exactly one pump loop runs at a
/// time; switching sources flushes the sink so stale audio is not played on
/// the new source's timeline.
/// </summary>
public sealed class AudioPump(ISourceArbiter arbiter, IAudioSink sink, PlaybackStatus status, ILogger<AudioPump> logger) : IAsyncDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _loopCts;
    private Task _loop = Task.CompletedTask;
    private CancellationToken _lifetime;

    public void Start(CancellationToken lifetime)
    {
        _lifetime = lifetime;
        arbiter.ActiveSourceChanged += OnActiveSourceChanged;
        OnActiveSourceChanged(this, arbiter.ActiveSource);
    }

    private void OnActiveSourceChanged(object? sender, IAudioSource? source)
    {
        lock (_gate)
        {
            _loopCts?.Cancel();
            _loopCts = null;

            if (source is null || _lifetime.IsCancellationRequested)
            {
                return;
            }

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            var previous = _loop;
            _loop = PumpAsync(source, previous, _loopCts.Token);
        }
    }

    private async Task PumpAsync(IAudioSource source, Task previousLoop, CancellationToken ct)
    {
        try
        {
            await previousLoop.ConfigureAwait(false);
        }
        catch
        {
            // Previous loop's failure was already logged; never block the new source on it.
        }

        void OnVolume(object? _, float volume)
        {
            // Display only — source volume is NOT forwarded to the sink.
            // librespot's softvol already applies the Spotify slider to the
            // PCM itself; forwarding it to the speakers as well applied the
            // volume twice (mid-slider became near-silence) and drove the
            // speakers' real volume from every slider wiggle. Speaker volume
            // is only ever set explicitly via the web UI/API.
            status.Volume = volume;
        }

        void OnMetadata(object? _, TrackMetadata metadata)
        {
            status.Metadata = metadata;
            Forward(() => sink.SetMetadataAsync(metadata, ct), "metadata");
        }

        var dropQueued = 0;
        var controlChain = Task.CompletedTask;
        var controlGate = new object();

        // Control ops must run IN ORDER, each completing before the next: a
        // pause's rate-0/flush RTSP sequence takes up to seconds, and a
        // fire-and-forget resume overtook it — the fresh rate-1 anchors went
        // out first and the stale rate-0 then stopped the clocks again
        // ("plays a second, dies" on quick pause/play).
        void EnqueueControl(Func<Task> op, string what)
        {
            lock (controlGate)
            {
                var previous = controlChain;
                controlChain = RunAsync();

                async Task RunAsync()
                {
                    try
                    {
                        await previous.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Logged by its own step.
                    }

                    try
                    {
                        await op().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Sink rejected {What} update", what);
                    }
                }
            }
        }

        void OnState(object? _, SourceStateChanged change)
        {
            // Pause/idle: silence the speakers now (they hold seconds of
            // already-shipped audio) but KEEP everything queued — it is
            // unheard audio, and resume plays it first so the listener
            // continues from where the sound stopped, not from the source's
            // read-ahead position.
            if (change.NewState is SourceState.Paused or SourceState.Idle)
            {
                EnqueueControl(() => sink.FlushAsync(dropQueuedAudio: false, ct), "flush");
            }
            else if (change.NewState is SourceState.Active)
            {
                // Any transition INTO Active resumes, not just Paused→Active:
                // the flush that held the pipeline may have come from an Idle
                // drop (network blip killing the source), and Idle→Active then
                // has to release that hold or the send loop stays wedged.
                // Resume is idempotent when nothing is held.
                EnqueueControl(() => sink.ResumeAsync(ct), "resume");
            }
        }

        void OnDiscontinuity(object? _, EventArgs __)
        {
            // Skip/new load: the queued tail belongs to the abandoned track.
            // Drop it everywhere — the sink's queues via the drop-flush, the
            // source's own queue via the frame loop (its single reader).
            Interlocked.Exchange(ref dropQueued, 1);
            EnqueueControl(() => sink.FlushAsync(dropQueuedAudio: true, ct), "drop-flush");
        }

        try
        {
            status.ActiveSource = source.Name;
            logger.LogInformation("Pumping {Source} into sink", source.Name);
            await sink.FlushAsync(ct).ConfigureAwait(false);
            await sink.StartStreamAsync(AudioFormat.Canonical, ct).ConfigureAwait(false);

            source.VolumeChanged += OnVolume;
            source.MetadataChanged += OnMetadata;
            source.StateChanged += OnState;
            source.Discontinuity += OnDiscontinuity;

            await foreach (var frame in source.Frames.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref dropQueued, 0) == 1)
                {
                    while (source.Frames.TryRead(out _))
                    {
                    }

                    continue; // the in-hand frame predates the cutover too
                }

                await sink.WriteAsync(frame, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Source switch or shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pump loop for {Source} failed", source.Name);
        }
        finally
        {
            source.VolumeChanged -= OnVolume;
            source.MetadataChanged -= OnMetadata;
            source.StateChanged -= OnState;
            source.Discontinuity -= OnDiscontinuity;
            // Only clear if a newer loop hasn't already claimed the display.
            if (status.ActiveSource == source.Name)
            {
                status.ActiveSource = null;
            }

            try
            {
                await sink.StopStreamAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sink failed to stop cleanly");
            }
        }
    }

    private void Forward(Func<Task> action, string what)
    {
        _ = action().ContinueWith(
            t => logger.LogWarning(t.Exception!.GetBaseException(), "Sink rejected {What} update", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        arbiter.ActiveSourceChanged -= OnActiveSourceChanged;
        Task loop;
        lock (_gate)
        {
            _loopCts?.Cancel();
            loop = _loop;
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch
        {
            // Already logged inside the loop.
        }
    }
}
