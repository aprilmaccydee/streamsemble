using System.Buffers.Binary;

namespace Streamsemble.Core.Video;

/// <summary>One NAL unit's position inside an access unit, and what kind it is.</summary>
public readonly record struct H264NalUnit(int Offset, int Length, byte Type)
{
    public bool IsIdrSlice => Type == H264Nal.IdrSlice;

    public bool IsParameterSet => Type is H264Nal.SequenceParameterSet or H264Nal.PictureParameterSet;
}

/// <summary>
/// NAL-level operations on H.264 access units. AirPlay mirroring carries AVCC
/// (4-byte big-endian length prefixes); files and ffmpeg want Annex-B (start
/// codes). Both conversions are here so the video path can stay in AVCC —
/// passthrough's whole point — and convert only at the edges where something
/// external needs to read it.
/// </summary>
public static class H264Nal
{
    public const byte NonIdrSlice = 1;
    public const byte IdrSlice = 5;
    public const byte SupplementalEnhancementInfo = 6;
    public const byte SequenceParameterSet = 7;
    public const byte PictureParameterSet = 8;
    public const byte AccessUnitDelimiter = 9;

    /// <summary>
    /// Walks the length-prefixed NAL units in an access unit. Throws when a
    /// prefix runs past the buffer, which is the earliest and clearest signal
    /// that a stream decrypted to garbage.
    /// </summary>
    public static List<H264NalUnit> Split(ReadOnlySpan<byte> accessUnit)
    {
        var units = new List<H264NalUnit>();
        var offset = 0;
        while (offset + VideoFrame.LengthPrefixBytes <= accessUnit.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(accessUnit[offset..]);
            offset += VideoFrame.LengthPrefixBytes;
            if (length <= 0 || offset + length > accessUnit.Length)
            {
                throw new InvalidDataException(
                    $"NAL length {length} at offset {offset} runs past the {accessUnit.Length} B access unit");
            }

            units.Add(new H264NalUnit(offset, length, (byte)(accessUnit[offset] & 0x1F)));
            offset += length;
        }

        if (offset != accessUnit.Length)
        {
            throw new InvalidDataException(
                $"access unit has {accessUnit.Length - offset} trailing bytes after its last NAL unit");
        }

        return units;
    }

    /// <summary>
    /// Whether a buffer is plausibly an AVCC access unit. Used as the acceptance
    /// test when settling which key a stream is encrypted with: the length
    /// prefixes have to tile the buffer exactly and every NAL type has to be a
    /// real one, which noise from a wrong key effectively never satisfies.
    /// </summary>
    public static bool LooksLikeAvcc(ReadOnlySpan<byte> accessUnit)
    {
        List<H264NalUnit> units;
        try
        {
            units = Split(accessUnit);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        // Type 0 and the reserved types above 12 do not occur in a mirror
        // stream; seeing one means the prefixes tiled by coincidence.
        return units.Count > 0 && units.All(u => u.Type is > 0 and <= 12);
    }

    /// <summary>
    /// Strips an access unit down to its VCL slices (types 1 and 5), re-framed
    /// as AVCC. AirPlay mirroring carries the parameter sets (SPS/PPS) and SEI
    /// out of band, in the one avcC codec packet; a tvOS mirror decoder is
    /// configured from that record and expects each video packet to hold coded
    /// slices ONLY. Handed parameter sets inline — which our inbound access
    /// units carry, and which the receiver's assembler even injects ahead of a
    /// keyframe — it renders nothing. This is the sender-side counterpart of
    /// that injection: real senders send exactly the VCL units and no more.
    /// Returns an empty array when the unit has no coded slice (a config-only
    /// AU), which the caller skips rather than sending an empty packet.
    /// </summary>
    public static byte[] VclOnly(ReadOnlySpan<byte> accessUnit)
    {
        var units = Split(accessUnit);

        var total = 0;
        foreach (var unit in units)
        {
            if (unit.Type is NonIdrSlice or IdrSlice)
            {
                total += VideoFrame.LengthPrefixBytes + unit.Length;
            }
        }

        if (total == 0)
        {
            return [];
        }

        var output = new byte[total];
        var offset = 0;
        foreach (var unit in units)
        {
            if (unit.Type is not (NonIdrSlice or IdrSlice))
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset), (uint)unit.Length);
            offset += VideoFrame.LengthPrefixBytes;
            accessUnit.Slice(unit.Offset, unit.Length).CopyTo(output.AsSpan(offset));
            offset += unit.Length;
        }

        return output;
    }

    /// <summary>True when the access unit carries an IDR slice — a decoder can start here.</summary>
    public static bool ContainsIdr(ReadOnlySpan<byte> accessUnit)
    {
        foreach (var unit in Split(accessUnit))
        {
            if (unit.IsIdrSlice)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>AVCC → Annex-B: every length prefix replaced by a 4-byte start code.</summary>
    public static byte[] ToAnnexB(ReadOnlySpan<byte> accessUnit)
    {
        var output = accessUnit.ToArray();
        foreach (var unit in Split(accessUnit))
        {
            var prefix = output.AsSpan(unit.Offset - VideoFrame.LengthPrefixBytes, VideoFrame.LengthPrefixBytes);
            prefix.Clear();
            prefix[3] = 1;
        }

        return output;
    }

    /// <summary>
    /// Annex-B → AVCC, accepting both 3- and 4-byte start codes. Used to bring
    /// externally generated H.264 (an ffmpeg test pattern) onto the wire format
    /// the mirror protocol speaks.
    /// </summary>
    public static byte[] FromAnnexB(ReadOnlySpan<byte> annexB)
    {
        var starts = new List<(int Start, int PrefixLength)>();
        for (var i = 0; i + 2 < annexB.Length; i++)
        {
            if (annexB[i] != 0 || annexB[i + 1] != 0)
            {
                continue;
            }

            if (annexB[i + 2] == 1)
            {
                starts.Add((i + 3, 3));
                i += 2;
            }
            else if (annexB[i + 2] == 0 && i + 3 < annexB.Length && annexB[i + 3] == 1)
            {
                starts.Add((i + 4, 4));
                i += 3;
            }
        }

        using var output = new MemoryStream();
        Span<byte> prefix = stackalloc byte[VideoFrame.LengthPrefixBytes];
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i].Start;
            var end = i + 1 < starts.Count ? starts[i + 1].Start - starts[i + 1].PrefixLength : annexB.Length;
            var length = end - start;
            if (length <= 0)
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)length);
            output.Write(prefix);
            output.Write(annexB.Slice(start, length));
        }

        return output.ToArray();
    }

    /// <summary>Wraps a single NAL unit's bytes as a one-unit AVCC access unit.</summary>
    public static byte[] ToAvcc(ReadOnlySpan<byte> nal)
    {
        var framed = new byte[VideoFrame.LengthPrefixBytes + nal.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)nal.Length);
        nal.CopyTo(framed.AsSpan(VideoFrame.LengthPrefixBytes));
        return framed;
    }
}
