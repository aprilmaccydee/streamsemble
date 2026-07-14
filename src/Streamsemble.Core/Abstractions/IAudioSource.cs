using System.Threading.Channels;
using Streamsemble.Core.Audio;
using Streamsemble.Core.Metadata;

namespace Streamsemble.Core.Abstractions;

public enum SourceState
{
    Idle,
    Active,
    Paused,
}

public sealed record SourceStateChanged(SourceState OldState, SourceState NewState);

/// <summary>
/// An inbound audio protocol endpoint (Spotify Connect, AirPlay 2 receiver,
/// Google Cast). Emits canonical-format PCM frames while a remote app is
/// streaming to us.
/// </summary>
public interface IAudioSource
{
    string Name { get; }

    SourceState State { get; }

    /// <summary>
    /// Canonical-format PCM. Bounded: when no consumer is draining (source is
    /// not the arbiter's live source), writers drop oldest frames rather than
    /// stall the protocol session.
    /// </summary>
    ChannelReader<PcmFrame> Frames { get; }

    event EventHandler<SourceStateChanged>? StateChanged;
    event EventHandler<TrackMetadata>? MetadataChanged;

    /// <summary>Volume in [0, 1] requested by the remote app.</summary>
    event EventHandler<float>? VolumeChanged;

    /// <summary>
    /// The arbiter asks this source to yield because another source went live.
    /// Best effort: pause/disconnect the remote session where the protocol
    /// allows it, and stop emitting frames either way.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
