using System.Threading.Channels;

namespace Streamsemble.Core.Video;

/// <summary>
/// Shared plumbing for video sources: a bounded drop-oldest frame channel and
/// the config/active bookkeeping. Capacity is about four seconds of 60 fps
/// video: when the audio group's timeline runs uniformly late (a realtime
/// mirror source against a large group latency), the video sink deliberately
/// holds each frame until one send-lead before its shifted stamp, so up to a
/// group latency's worth of live frames queues HERE by design — it is the
/// buffer the display doesn't have. Still bounded, so a consumer that has
/// genuinely died sheds frames instead of growing a stale backlog.
/// </summary>
public abstract class VideoSourceBase(string name) : IVideoSource
{
    private const int ChannelCapacityFrames = 256;

    private readonly Channel<VideoFrame> _channel = Channel.CreateBounded<VideoFrame>(
        new BoundedChannelOptions(ChannelCapacityFrames)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    public string Name { get; } = name;

    public bool IsActive { get; private set; }

    public ChannelReader<VideoFrame> Frames => _channel.Reader;

    public VideoCodecConfig? CodecConfig { get; private set; }

    public event EventHandler<VideoCodecConfig>? CodecConfigChanged;

    public event EventHandler<bool>? ActiveChanged;

    protected void EmitFrame(VideoFrame frame) => _channel.Writer.TryWrite(frame);

    protected void SetCodecConfig(VideoCodecConfig config)
    {
        CodecConfig = config;
        CodecConfigChanged?.Invoke(this, config);
    }

    protected void SetActive(bool active)
    {
        if (active == IsActive)
        {
            return;
        }

        IsActive = active;
        if (!active)
        {
            // A stopped source must not leave a tail behind: whatever is queued
            // is content from a session that has ended, and every frame in it
            // carries a deadline that has already passed.
            while (_channel.Reader.TryRead(out _))
            {
            }
        }

        ActiveChanged?.Invoke(this, active);
    }
}
