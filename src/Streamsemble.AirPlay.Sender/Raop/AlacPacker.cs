namespace Streamsemble.AirPlay.Sender.Raop;

/// <summary>
/// Wraps raw PCM in an ALAC "verbatim" (uncompressed) frame — the standard
/// RAOP sender trick: a 23-bit ALAC frame header declaring an uncompressed
/// payload, followed by the samples as 16-bit big-endian. No real encoder is
/// involved.
/// </summary>
public static class AlacPacker
{
    /// <summary>Output size for <paramref name="sampleCount"/> stereo 16-bit samples (incl. 3-bit end tag).</summary>
    public static int PackedLength(int sampleCount) => (23 + sampleCount * 32 + 3 + 7) / 8;

    /// <summary>
    /// Packs S16LE interleaved stereo PCM into an ALAC verbatim frame.
    /// Returns bytes written to <paramref name="output"/>.
    /// </summary>
    public static int Pack(ReadOnlySpan<byte> pcmS16Le, Span<byte> output)
    {
        output.Clear();
        var writer = new BitWriter(output);

        // Per the ALAC frame layout (see OwnTone raop.c alac_encode):
        writer.Write(1, 3);  // channels - 1? stereo tag used by every RAOP sender
        writer.Write(0, 4);
        writer.Write(0, 8);
        writer.Write(0, 4);
        writer.Write(0, 1);  // has-size
        writer.Write(0, 2);  // unused
        writer.Write(1, 1);  // is-not-compressed

        for (var i = 0; i < pcmS16Le.Length; i += 2)
        {
            // S16LE in, 16-bit big-endian out.
            writer.Write((uint)(pcmS16Le[i + 1] << 8 | pcmS16Le[i]), 16);
        }

        writer.Write(7, 3); // end tag (0b111) — required to close the ALAC frame

        return writer.BytesWritten;
    }

    private ref struct BitWriter(Span<byte> output)
    {
        private readonly Span<byte> _output = output;
        private int _bitPosition;

        public readonly int BytesWritten => (_bitPosition + 7) / 8;

        public void Write(uint value, int bitCount)
        {
            for (var bit = bitCount - 1; bit >= 0; bit--)
            {
                if ((value & (1u << bit)) != 0)
                {
                    _output[_bitPosition / 8] |= (byte)(0x80 >> (_bitPosition % 8));
                }

                _bitPosition++;
            }
        }
    }
}
