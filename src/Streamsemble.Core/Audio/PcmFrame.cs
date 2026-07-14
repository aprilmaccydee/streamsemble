namespace Streamsemble.Core.Audio;

/// <summary>
/// A timestamped block of canonical-format PCM.
/// </summary>
/// <param name="Data">Interleaved S16LE stereo samples.</param>
/// <param name="Timestamp">
/// Monotonic sample-clock position of the first sample in this frame
/// (a running sample counter, not wall time). On the sender side this
/// becomes the RTP timestamp, which is why it is threaded through the
/// whole pipeline: multi-room sync depends on every target seeing the
/// same timestamp for the same audio.
/// </param>
public readonly record struct PcmFrame(ReadOnlyMemory<byte> Data, long Timestamp)
{
    /// <summary>Native RAOP packet size; the pipeline's standard frame length.</summary>
    public const int SamplesPerFrame = 352;

    public static readonly int CanonicalFrameBytes = AudioFormat.Canonical.BytesForSamples(SamplesPerFrame);

    public int SampleCount => Data.Length / AudioFormat.Canonical.BlockAlign;
}
