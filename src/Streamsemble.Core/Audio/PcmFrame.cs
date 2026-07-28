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
/// <param name="TargetNanos">
/// Absolute grandmaster time this frame's first sample should turn
/// AUDIBLE, or 0 when the source has no opinion (music). A live source
/// (inbound AirPlay) knows each frame's render deadline exactly; carrying
/// it with the data lets the sink derive its whole send timeline from the
/// source's intent instead of estimating pipeline delays — the sink owns
/// every hop downstream, so obeying the stamp is exact by construction.
/// </param>
public readonly record struct PcmFrame(ReadOnlyMemory<byte> Data, long Timestamp, long TargetNanos = 0)
{
    /// <summary>Native RAOP packet size; the pipeline's standard frame length.</summary>
    public const int SamplesPerFrame = 352;

    public static readonly int CanonicalFrameBytes = AudioFormat.Canonical.BytesForSamples(SamplesPerFrame);

    public int SampleCount => Data.Length / AudioFormat.Canonical.BlockAlign;
}
