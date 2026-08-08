using Streamsemble.Core.Video;

namespace Streamsemble.AirPlay.Receiver.Video;

/// <summary>
/// The video half of the AirPlay receiver: what a Mac's screen mirror turns
/// into once it has been decrypted and stamped. The audio half of the same
/// mirror session keeps flowing through
/// <see cref="AirPlayReceiverSource"/> unchanged, so picture and sound reach
/// their sinks on the one grandmaster timeline the hub already runs.
/// </summary>
public sealed class MirrorVideoSource() : VideoSourceBase("AirPlay Mirror")
{
    /// <summary>Access units delivered to the pipeline since the process started.</summary>
    public long FramesEmitted { get; private set; }

    /// <summary>Last frame's render stamp, for the telemetry panel.</summary>
    public long LastTargetNanos { get; private set; }

    internal void PushAccessUnit(VideoFrame frame)
    {
        FramesEmitted++;
        LastTargetNanos = frame.TargetNanos;
        EmitFrame(frame);
    }

    internal void PushCodecConfig(VideoCodecConfig config) => SetCodecConfig(config);

    internal void MarkActive() => SetActive(true);

    internal void MarkIdle() => SetActive(false);
}
