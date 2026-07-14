using System.Threading.Channels;
using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Metadata;

namespace Streamsemble.Core.Audio;

/// <summary>
/// Shared plumbing for sources: bounded drop-oldest frame channel (~2 s), the
/// running sample-clock timestamp, and state/event bookkeeping.
/// </summary>
public abstract class AudioSourceBase(string name) : IAudioSource
{
    private const int ChannelCapacityFrames = 250; // ≈ 2 s of 352-sample frames

    private readonly Channel<PcmFrame> _channel = Channel.CreateBounded<PcmFrame>(
        new BoundedChannelOptions(ChannelCapacityFrames)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private long _sampleClock;

    public string Name { get; } = name;

    public SourceState State { get; private set; } = SourceState.Idle;

    public ChannelReader<PcmFrame> Frames => _channel.Reader;

    public event EventHandler<SourceStateChanged>? StateChanged;
    public event EventHandler<TrackMetadata>? MetadataChanged;
    public event EventHandler<float>? VolumeChanged;

    public abstract Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Stamps <paramref name="pcm"/> with the running sample clock and queues it.</summary>
    protected void EmitPcm(ReadOnlyMemory<byte> pcm)
    {
        var frame = new PcmFrame(pcm, _sampleClock);
        _sampleClock += frame.SampleCount;
        _channel.Writer.TryWrite(frame);
    }

    protected void SetState(SourceState newState)
    {
        if (newState == State)
        {
            return;
        }

        var change = new SourceStateChanged(State, newState);
        State = newState;
        StateChanged?.Invoke(this, change);
    }

    protected void RaiseMetadata(TrackMetadata metadata) => MetadataChanged?.Invoke(this, metadata);

    protected void RaiseVolume(float volume) => VolumeChanged?.Invoke(this, Math.Clamp(volume, 0f, 1f));
}
