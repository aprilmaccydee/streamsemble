using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Audio;
using Streamsemble.Core.Metadata;

namespace Streamsemble.Wled;

/// <summary>
/// Transparent sink wrapper that copies the frame stream — and the flush/stop
/// signals that give it meaning — to the lighting service on its way to the
/// real sink. The lights see exactly the audio the speakers were sent.
/// </summary>
public sealed class LightingTapSink(IAudioSink inner, WledLightingService lighting) : IAudioSink
{
    public Task StartStreamAsync(AudioFormat format, CancellationToken cancellationToken = default)
        => inner.StartStreamAsync(format, cancellationToken);

    public ValueTask WriteAsync(PcmFrame frame, CancellationToken cancellationToken = default)
    {
        lighting.Ingest(frame);
        return inner.WriteAsync(frame, cancellationToken);
    }

    public Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
        => inner.SetVolumeAsync(volume, cancellationToken);

    public Task SetMetadataAsync(TrackMetadata metadata, CancellationToken cancellationToken = default)
        => inner.SetMetadataAsync(metadata, cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lighting.NotifyFlush(dropQueuedAudio: true);
        return inner.FlushAsync(cancellationToken);
    }

    public Task FlushAsync(bool dropQueuedAudio, CancellationToken cancellationToken = default)
    {
        lighting.NotifyFlush(dropQueuedAudio);
        return inner.FlushAsync(dropQueuedAudio, cancellationToken);
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => inner.ResumeAsync(cancellationToken);

    public Task StopStreamAsync(CancellationToken cancellationToken = default)
    {
        lighting.NotifyStreamStop();
        return inner.StopStreamAsync(cancellationToken);
    }
}
