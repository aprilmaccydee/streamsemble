using Streamsemble.Core.Audio;
using Streamsemble.AirPlay.Receiver.Audio;

namespace Streamsemble.AirPlay.Receiver;

/// <summary>
/// AirPlay 2 receiver source. Scaffolding: advertises the device and accepts
/// RTSP connections; the pairing crypto (SRP transient pair-setup, pair-verify,
/// fp-setup), PTP timing and the buffered-audio decrypt/decode path that turn
/// an inbound stream into <see cref="EmitPcm"/> calls are the M3 work items
/// (see README). The source slot exists so the arbiter and host wiring are
/// complete today.
/// </summary>
public sealed class AirPlayReceiverSource() : AudioSourceBase("AirPlay")
{
    private readonly object _pcmGate = new();
    private byte[] _pendingPcm = new byte[PcmFrame.CanonicalFrameBytes];
    private int _pendingBytes;
    private long _pendingTargetNanos;
    private AnchoredPcmScheduler? _realtimeScheduler;

    public long RealtimeConcealedSamples => Volatile.Read(ref _realtimeScheduler)?.ConcealedSamples ?? 0;

    internal void SetRealtimeScheduler(AnchoredPcmScheduler? scheduler)
        => Volatile.Write(ref _realtimeScheduler, scheduler);

    public override Task StopAsync(CancellationToken cancellationToken = default)
    {
        MarkIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Entry point for the decoded-audio path to feed the pipeline.
    /// <paramref name="targetNanos"/> is the grandmaster time the first
    /// sample should turn audible (0 = unknown); the sink derives its send
    /// timeline from it so inbound audio presents exactly when the sender
    /// asked.
    /// </summary>
    internal void PushDecodedPcm(ReadOnlyMemory<byte> pcm, long targetNanos = 0)
    {
        lock (_pcmGate)
        {
            // AAC access units and concealed losses need not be multiples of
            // 352. Downstream ALAC sessions negotiate that fixed packet size,
            // so keep the final partial frame here until its remaining samples
            // arrive, without dropping or padding any portion of the timeline.
            var offset = 0;
            while (offset < pcm.Length)
            {
                if (_pendingBytes == 0)
                {
                    _pendingTargetNanos = targetNanos > 0
                        ? targetNanos + (offset / AudioFormat.Canonical.BlockAlign) * 1_000_000_000L
                            / AudioFormat.Canonical.SampleRate
                        : 0;
                }

                var count = Math.Min(pcm.Length - offset, _pendingPcm.Length - _pendingBytes);
                pcm.Span.Slice(offset, count).CopyTo(_pendingPcm.AsSpan(_pendingBytes));
                _pendingBytes += count;
                offset += count;
                if (_pendingBytes == _pendingPcm.Length)
                {
                    EmitPcm(_pendingPcm, _pendingTargetNanos);
                    _pendingPcm = new byte[PcmFrame.CanonicalFrameBytes];
                    _pendingBytes = 0;
                }
            }
        }
    }

    internal void MarkActive()
    {
        lock (_pcmGate)
        {
            SetState(Core.Abstractions.SourceState.Active);
        }
    }

    /// <summary>
    /// Sender pause/seek (FLUSH/FLUSHBUFFERED). Routes through the pump's
    /// Paused handling so the speaker fan-out flushes too — without this the
    /// group latency's worth of in-flight audio plays out after the sender
    /// stopped. The next SETRATEANCHORTIME rate=1 marks Active again.
    /// </summary>
    internal void MarkPaused() => ResetPcm(Core.Abstractions.SourceState.Paused);

    internal void MarkIdle() => ResetPcm(Core.Abstractions.SourceState.Idle);

    private void ResetPcm(Core.Abstractions.SourceState state)
    {
        lock (_pcmGate)
        {
            _pendingBytes = 0;
            _pendingTargetNanos = 0;
            SetState(state);
        }
    }

    internal void PushMetadata(Core.Metadata.TrackMetadata metadata) => RaiseMetadata(metadata);
}
