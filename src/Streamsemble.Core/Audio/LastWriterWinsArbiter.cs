using Microsoft.Extensions.Logging;
using Streamsemble.Core.Abstractions;

namespace Streamsemble.Core.Audio;

/// <summary>
/// Last source to start playing wins; the previous live source is asked to
/// stop. A live source that goes idle simply releases the slot (a paused
/// source stays live so resume doesn't re-arbitrate).
/// </summary>
public sealed class LastWriterWinsArbiter(ILogger<LastWriterWinsArbiter> logger) : ISourceArbiter
{
    private readonly object _gate = new();
    private IAudioSource? _active;

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
            if (change.NewState == SourceState.Active && !ReferenceEquals(_active, source))
            {
                toStop = _active;
                _active = source;
                changed = true;
            }
            else if (change.NewState == SourceState.Idle && ReferenceEquals(_active, source))
            {
                _active = null;
                changed = true;
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
