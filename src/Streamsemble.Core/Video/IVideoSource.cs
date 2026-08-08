using System.Threading.Channels;

namespace Streamsemble.Core.Video;

/// <summary>
/// An inbound video endpoint — today the AirPlay screen-mirroring receiver.
/// Mirrors <see cref="Core.Abstractions.IAudioSource"/>: frames arrive on a
/// bounded channel that drops oldest rather than stalling the protocol session
/// when nothing is draining it.
/// </summary>
public interface IVideoSource
{
    string Name { get; }

    /// <summary>True while a sender is actually mirroring to us.</summary>
    bool IsActive { get; }

    /// <summary>Stamped H.264 access units in decode order.</summary>
    ChannelReader<VideoFrame> Frames { get; }

    /// <summary>
    /// The parameter sets currently in force, or null before the first config
    /// packet. A sink joining mid-stream reads this to prime its decoder rather
    /// than waiting for the sender to volunteer another one.
    /// </summary>
    VideoCodecConfig? CodecConfig { get; }

    /// <summary>Raised when the sender changes resolution or parameter sets.</summary>
    event EventHandler<VideoCodecConfig>? CodecConfigChanged;

    /// <summary>Raised with true when mirroring starts and false when it stops.</summary>
    event EventHandler<bool>? ActiveChanged;
}
