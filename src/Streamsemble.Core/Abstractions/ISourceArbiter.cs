namespace Streamsemble.Core.Abstractions;

/// <summary>
/// Decides which registered source is live when several remote apps try to
/// play at once. Policy: last source to start playing wins; the previous live
/// source is told to stop.
/// </summary>
public interface ISourceArbiter
{
    IAudioSource? ActiveSource { get; }

    event EventHandler<IAudioSource?>? ActiveSourceChanged;

    void Register(IAudioSource source);
}
