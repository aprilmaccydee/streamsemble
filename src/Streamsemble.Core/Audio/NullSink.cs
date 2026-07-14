using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Metadata;

namespace Streamsemble.Core.Audio;

public sealed class NullSink : IAudioSink
{
    public Task StartStreamAsync(AudioFormat format, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask WriteAsync(PcmFrame frame, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetMetadataAsync(TrackMetadata metadata, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopStreamAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
