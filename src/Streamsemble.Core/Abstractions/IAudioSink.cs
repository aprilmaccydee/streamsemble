using Streamsemble.Core.Audio;
using Streamsemble.Core.Metadata;

namespace Streamsemble.Core.Abstractions;

/// <summary>
/// An outbound audio destination — the AirPlay target group in production,
/// WAV/null sinks for testing.
/// </summary>
public interface IAudioSink
{
    Task StartStreamAsync(AudioFormat format, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(PcmFrame frame, CancellationToken cancellationToken = default);

    /// <summary>Volume in [0, 1]; sinks map to their protocol's scale (e.g. RAOP dB attenuation).</summary>
    Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default);

    Task SetMetadataAsync(TrackMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>Discard buffered/in-flight audio (seek, source switch, pause).</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Flush with control over queued-but-unsent audio: dropQueuedAudio=true
    /// (skip) discards it; false (pause) keeps it so playback resumes from
    /// the audibly-last position. Default implementation ignores the flag.
    /// </summary>
    Task FlushAsync(bool dropQueuedAudio, CancellationToken cancellationToken = default) => FlushAsync(cancellationToken);

    /// <summary>
    /// Resume after a pause-flush: release held frames and re-anchor so the
    /// preserved tail plays from the audibly-last position. No-op by default.
    /// </summary>
    Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task StopStreamAsync(CancellationToken cancellationToken = default);
}
