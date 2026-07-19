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

        void OnState(object? _, SourceStateChanged change)
        {
            // Buffered sinks hold seconds of already-shipped audio; without a
            // flush, pause keeps playing until the speaker buffer drains and a
            // track change plays the old song's tail first. The sink
            // re-anchors its timeline on the next packet after the flush.
            if (change.NewState is SourceState.Paused or SourceState.Idle)
            {
                Forward(() => sink.FlushAsync(ct), "flush");
            }
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

            await foreach (var frame in source.Frames.ReadAllAsync(ct).ConfigureAwait(false))
            {
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
