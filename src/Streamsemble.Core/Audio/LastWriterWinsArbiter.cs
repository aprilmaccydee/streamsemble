using Microsoft.Extensions.Logging;
using Streamsemble.Core.Abstractions;

namespace Streamsemble.Core.Audio;

/// <summary>
/// Last source to start playing wins; the previous live source is asked to
/// stop. A paused source stays live so resume doesn't re-arbitrate. A live
/// source that goes idle releases the slot only after a grace period: an
/// Idle drop is more often transient (Spotify's websocket drops and librespot
/// reconnects within seconds) than a real stop, and releasing immediately
/// tore down every speaker session just to rebuild it moments later.
/// </summary>
public sealed class LastWriterWinsArbiter(ILogger<LastWriterWinsArbiter> logger, TimeSpan? idleGrace = null) : ISourceArbiter
{
    private readonly TimeSpan _idleGrace = idleGrace ?? TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private IAudioSource? _active;
    private CancellationTokenSource? _idleRelease;

    public IAudioSource? ActiveSource
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public event EventHandler<IAudioSource?>? ActiveSourceChanged;

    public void Register(IAudioSource source)
    {
        source.StateChanged += (_, change) => OnSourceStateChanged(source, change);
        logger.LogInformation("Registered source {Source}", source.Name);
    }

    private void OnSourceStateChanged(IAudioSource source, SourceStateChanged change)
    {
        IAudioSource? toStop = null;
        var changed = false;

        lock (_gate)
        {
            if (change.NewState == SourceState.Active)
            {
                _idleRelease?.Cancel();
                _idleRelease = null;
                if (!ReferenceEquals(_active, source))
                {
                    toStop = _active;
                    _active = source;
                    changed = true;
                }
            }
            else if (change.NewState == SourceState.Idle && ReferenceEquals(_active, source))
            {
                _idleRelease?.Cancel();
                var cts = new CancellationTokenSource();
                _idleRelease = cts;
                _ = ReleaseAfterGraceAsync(source, cts);
            }
        }

        if (!changed)
        {
            return;
        }

        logger.LogInformation("Live source is now {Source}", ActiveSource?.Name ?? "(none)");
        ActiveSourceChanged?.Invoke(this, ActiveSource);

        if (toStop is not null)
        {
            _ = StopPreemptedAsync(toStop);
        }
    }

    private async Task ReleaseAfterGraceAsync(IAudioSource source, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_idleGrace, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // source came back (or was preempted) within the grace window
        }

        var changed = false;
        lock (_gate)
        {
            if (!cts.IsCancellationRequested && ReferenceEquals(_active, source) && source.State == SourceState.Idle)
            {
                _active = null;
                _idleRelease = null;
                changed = true;
            }
        }

        if (changed)
        {
            logger.LogInformation("Live source is now (none) — {Source} stayed idle past the {Grace}s grace", source.Name, _idleGrace.TotalSeconds);
            ActiveSourceChanged?.Invoke(this, null);
        }
    }

    private async Task StopPreemptedAsync(IAudioSource source)
    {
        try
        {
            await source.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Preempted source {Source} failed to stop", source.Name);
        }
    }
}
