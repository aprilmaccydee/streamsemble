using Streamsemble.Core.Metadata;

namespace Streamsemble.Core;

/// <summary>
/// A tiny snapshot of what's playing, updated by the pump and read by the web
/// API. Not the source of truth for control — just for display.
/// </summary>
public sealed class PlaybackStatus
{
    private readonly object _gate = new();
    private volatile string? _activeSource;
    private TrackMetadata _metadata = new();
    private float _volume = 0.5f;

    public string? ActiveSource
    {
        get => _activeSource;
        set => _activeSource = value;
    }

    public float Volume
    {
        get { lock (_gate) { return _volume; } }
        set { lock (_gate) { _volume = value; } }
    }

    public TrackMetadata Metadata
    {
        get { lock (_gate) { return _metadata; } }
        set { lock (_gate) { _metadata = value; } }
    }
}
