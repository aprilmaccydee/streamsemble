namespace Streamsemble.Core.Audio;

/// <summary>
/// Describes a PCM audio format. The pipeline's canonical format is
/// <see cref="Canonical"/> (44.1 kHz / 16-bit signed LE / stereo) — the native
/// RAOP wire format and librespot's native pipe output, so the happy path
/// never resamples.
/// </summary>
public readonly record struct AudioFormat(int SampleRate, int BitsPerSample, int Channels)
{
    public static readonly AudioFormat Canonical = new(44100, 16, 2);

    /// <summary>Bytes for one sample across all channels.</summary>
    public int BlockAlign => BitsPerSample / 8 * Channels;

    public int BytesForSamples(int sampleCount) => sampleCount * BlockAlign;
}
