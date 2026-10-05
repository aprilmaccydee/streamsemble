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
            // Explicit cutovers also discard queued PCM from the source's
            // thread, while the pump may already be waiting on a read.
            SingleReader = false,
        });

    private long _sampleClock;
    private readonly object _pcmStateGate = new();
    private long _generation;
    private bool _cutoverPrepared;

    public string Name { get; } = name;

    public SourceState State { get; private set; } = SourceState.Idle;

    public long Generation => Volatile.Read(ref _generation);

    public ChannelReader<PcmFrame> Frames => _channel.Reader;

    public event EventHandler<SourceStateChanged>? StateChanged;
    public event EventHandler? Discontinuity;
    public event EventHandler<TrackMetadata>? MetadataChanged;
    public event EventHandler<float>? VolumeChanged;

    public abstract Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Stamps <paramref name="pcm"/> with the running sample clock and queues it.</summary>
    protected void EmitPcm(ReadOnlyMemory<byte> pcm) => EmitPcm(pcm, 0);

    /// <summary>
    /// As <see cref="EmitPcm(ReadOnlyMemory{byte})"/>, additionally carrying
    /// the absolute grandmaster time the first sample should turn audible
    /// (see <see cref="PcmFrame.TargetNanos"/>); 0 = no target.
    /// </summary>
    protected void EmitPcm(ReadOnlyMemory<byte> pcm, long targetNanos)
    {
        lock (_pcmStateGate)
        {
            var frame = new PcmFrame(pcm, _sampleClock, targetNanos) { Generation = _generation };
            _sampleClock += frame.SampleCount;
            _channel.Writer.TryWrite(frame);
            _cutoverPrepared = false;
        }
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

    /// <summary>
    /// Abandons completed PCM from the old timeline. The generation also
    /// invalidates a frame the pump read just before this queue was drained.
    /// Call before publishing a discontinuity, never for an ordinary pause.
    /// </summary>
    protected void DiscardQueuedPcm()
    {
        lock (_pcmStateGate)
        {
            _generation++;
            while (_channel.Reader.TryRead(out _))
            {
            }

            _cutoverPrepared = true;
        }
    }

    protected void RaiseDiscontinuity()
    {
        lock (_pcmStateGate)
        {
            // Existing sources can announce a cutover directly. A protocol
            // that already discarded its queue need not advance twice.
            if (!_cutoverPrepared)
            {
                DiscardQueuedPcm();
            }

            _cutoverPrepared = false;
            // Publish the pump's flush barrier before the next EmitPcm can
            // expose a fresh frame from this generation.
            Discontinuity?.Invoke(this, EventArgs.Empty);
        }
    }

    protected void RaiseMetadata(TrackMetadata metadata) => MetadataChanged?.Invoke(this, metadata);

    protected void RaiseVolume(float volume) => VolumeChanged?.Invoke(this, Math.Clamp(volume, 0f, 1f));
}
