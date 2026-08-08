namespace Streamsemble.Core.Video;

/// <summary>
/// One H.264 access unit with the instant it should become visible.
/// </summary>
/// <param name="Data">
/// The access unit in AVCC form — every NAL unit preceded by its 4-byte
/// big-endian length, which is the layout AirPlay mirroring uses on the wire
/// in both directions. Keeping it in that form end to end is what makes
/// passthrough possible: the bytes a Mac encoded are the bytes the TV decodes.
/// </param>
/// <param name="TargetNanos">
/// Absolute grandmaster time this frame's picture should be ON SCREEN, or 0
/// when the source has no opinion. Identical semantics to
/// <see cref="Core.Audio.PcmFrame.TargetNanos"/> so picture and sound ride one
/// timeline: the source states the deadline, every sink downstream derives its
/// own send schedule from it, and nothing in between has to estimate.
/// </param>
/// <param name="IsKeyframe">
/// True when the unit contains an IDR slice — the only place a decoder can
/// start, so it is where a late-joining output session begins sending.
/// </param>
public readonly record struct VideoFrame(ReadOnlyMemory<byte> Data, long TargetNanos, bool IsKeyframe)
{
    /// <summary>Length prefix width of the AVCC framing used throughout the video path.</summary>
    public const int LengthPrefixBytes = 4;
}
