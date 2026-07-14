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

    Task StopStreamAsync(CancellationToken cancellationToken = default);
}
