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

    /// <summary>Entry point for the decoded-audio path to feed the pipeline.</summary>
    internal void PushDecodedPcm(ReadOnlyMemory<byte> pcm) => EmitPcm(pcm);

    internal void MarkActive() => SetState(Core.Abstractions.SourceState.Active);

    internal void MarkIdle() => SetState(Core.Abstractions.SourceState.Idle);

    internal void PushMetadata(Core.Metadata.TrackMetadata metadata) => RaiseMetadata(metadata);
}
