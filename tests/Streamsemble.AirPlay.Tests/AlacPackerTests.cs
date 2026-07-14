using Streamsemble.AirPlay.Sender.Raop;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class AlacPackerTests
{
    [Fact]
    public void PackedLengthMatchesBitMath()
    {
        // 23 header + 352*32 sample + 3 end-tag bits = 11290 bits → 1412 bytes.
        Assert.Equal(1412, AlacPacker.PackedLength(352));
    }

    [Fact]
    public void HeaderDeclaresUncompressedStereo()
    {
        var pcm = new byte[4]; // one stereo sample of silence
        var output = new byte[AlacPacker.PackedLength(1)];
        var written = AlacPacker.Pack(pcm, output);

        Assert.Equal((23 + 32 + 3 + 7) / 8, written);

        // Header bits: 001 0000 00000000 0000 0 00 1 → bytes 0x20, 0x00 and the
        // is-not-compressed flag as bit 22 (0x02 in byte 2).
        Assert.Equal(0x20, output[0]);
        Assert.Equal(0x00, output[1]);
        Assert.Equal(0x02, output[2]);
    }

    [Fact]
    public void SamplesAreByteSwappedToBigEndian()
    {
        // One stereo sample: L = 0x1234 (LE bytes 34 12), R = 0x5678 (LE bytes 78 56).
        byte[] pcm = [0x34, 0x12, 0x78, 0x56];
        var output = new byte[AlacPacker.PackedLength(1)];
        AlacPacker.Pack(pcm, output);

        // After the 23-bit header, samples land shifted by one bit; reconstruct
        // the 32-bit sample block from bits 23..55 and compare.
        var bits = string.Concat(output.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        var sampleBits = bits.Substring(23, 32);
        Assert.Equal("0001001000110100" + "0101011001111000", sampleBits);
    }
}
