using Streamsemble.Core.Audio;

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
    public override Task StopAsync(CancellationToken cancellationToken = default)
    {
        SetState(Core.Abstractions.SourceState.Idle);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Entry point for the decoded-audio path to feed the pipeline.
    /// <paramref name="targetNanos"/> is the grandmaster time the first
    /// sample should turn audible (0 = unknown); the sink derives its send
    /// timeline from it so inbound audio presents exactly when the sender
    /// asked.
    /// </summary>
    internal void PushDecodedPcm(ReadOnlyMemory<byte> pcm, long targetNanos = 0) => EmitPcm(pcm, targetNanos);

    internal void MarkActive() => SetState(Core.Abstractions.SourceState.Active);

    /// <summary>
    /// Sender pause/seek (FLUSH/FLUSHBUFFERED). Routes through the pump's
    /// Paused handling so the speaker fan-out flushes too — without this the
    /// group latency's worth of in-flight audio plays out after the sender
    /// stopped. The next SETRATEANCHORTIME rate=1 marks Active again.
    /// </summary>
    internal void MarkPaused() => SetState(Core.Abstractions.SourceState.Paused);

    internal void MarkIdle() => SetState(Core.Abstractions.SourceState.Idle);

    internal void PushMetadata(Core.Metadata.TrackMetadata metadata) => RaiseMetadata(metadata);
}
