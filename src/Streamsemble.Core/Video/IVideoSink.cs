namespace Streamsemble.Core.Video;

/// <summary>
/// An outbound video destination — the AirPlay mirror target group in
/// production. Mirrors <see cref="Core.Abstractions.IAudioSink"/>, with the
/// codec configuration taking the place of the audio format: a video sink
/// cannot open a stream without knowing the parameter sets that describe it.
/// </summary>
public interface IVideoSink
{
    Task StartStreamAsync(VideoCodecConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// The sender changed resolution or parameter sets mid-stream. Sinks
    /// forward the new config and expect a keyframe to follow.
    /// </summary>
    Task ReconfigureAsync(VideoCodecConfig config, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(VideoFrame frame, CancellationToken cancellationToken = default);

    Task StopStreamAsync(CancellationToken cancellationToken = default);
}
