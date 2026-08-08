using System.Buffers.Binary;

namespace Streamsemble.Core.Video;

/// <summary>
/// The decoder configuration for an H.264 mirror stream: the parameter sets a
/// decoder needs before the first slice, plus the picture size read out of the
/// SPS. AirPlay carries this as an ISO/IEC 14496-15 AVCDecoderConfigurationRecord
/// ("avcC") in its own unencrypted packet type, so the record round-trips
/// verbatim from the sender to the output session.
/// </summary>
public sealed record VideoCodecConfig(byte[] Sps, byte[] Pps, int Width, int Height)
{
    /// <summary>Profile/compatibility/level triple from the SPS, as the avcC header repeats them.</summary>
    public byte ProfileIdc => Sps.Length > 1 ? Sps[1] : (byte)0;

    public byte ProfileCompatibility => Sps.Length > 2 ? Sps[2] : (byte)0;

    public byte LevelIdc => Sps.Length > 3 ? Sps[3] : (byte)0;

    /// <summary>Human-readable summary for logs and the web UI, e.g. "1920x1080 H.264 high@4.0".</summary>
    public string Describe()
    {
        var profile = ProfileIdc switch
        {
            66 => "baseline",
            77 => "main",
            88 => "extended",
            100 => "high",
            110 => "high10",
            122 => "high422",
            244 => "high444",
            _ => $"profile{ProfileIdc}",
        };
        return $"{Width}x{Height} H.264 {profile}@{LevelIdc / 10.0:0.0}";
    }

    /// <summary>
    /// Reads an avcC record. Only the first SPS and PPS are kept: AirPlay
    /// senders emit exactly one of each, and a decoder fed a stream that
    /// switches parameter sets mid-flight gets a fresh config packet anyway.
    /// </summary>
    public static VideoCodecConfig Parse(ReadOnlySpan<byte> avcC)
    {
        if (avcC.Length < 7 || avcC[0] != 1)
        {
            throw new InvalidDataException(
                $"not an avcC record ({avcC.Length} B, version byte 0x{(avcC.Length > 0 ? avcC[0] : 0):x2})");
        }

        var offset = 5;
        var spsCount = avcC[offset++] & 0x1F;
        byte[]? sps = null;
        for (var i = 0; i < spsCount; i++)
        {
            var set = ReadParameterSet(avcC, ref offset);
            sps ??= set;
        }

        if (offset >= avcC.Length)
        {
            throw new InvalidDataException("avcC record ends before its picture parameter sets");
        }

        var ppsCount = avcC[offset++];
        byte[]? pps = null;
        for (var i = 0; i < ppsCount; i++)
        {
            var set = ReadParameterSet(avcC, ref offset);
            pps ??= set;
        }

        if (sps is null || pps is null)
        {
            throw new InvalidDataException("avcC record carries no SPS/PPS pair");
        }

        var (width, height) = H264SequenceParameterSet.PictureSize(sps);
        return new VideoCodecConfig(sps, pps, width, height);
    }

    private static byte[] ReadParameterSet(ReadOnlySpan<byte> avcC, ref int offset)
    {
        if (offset + 2 > avcC.Length)
        {
            throw new InvalidDataException("avcC parameter set length runs past the record");
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(avcC[offset..]);
        offset += 2;
        if (offset + length > avcC.Length)
        {
            throw new InvalidDataException($"avcC parameter set of {length} B runs past the record");
        }

        var set = avcC.Slice(offset, length).ToArray();
        offset += length;
        return set;
    }

    /// <summary>Serializes back to an avcC record — what an output mirror session sends as its config packet.</summary>
    public byte[] ToAvcC()
    {
        // 8 bytes of header before the SPS (version, profile triple, the two
        // packed reserved/count bytes, and the SPS length), then 3 more for the
        // PPS count and its length.
        var record = new byte[8 + Sps.Length + 3 + Pps.Length];
        record[0] = 1;
        record[1] = ProfileIdc;
        record[2] = ProfileCompatibility;
        record[3] = LevelIdc;
        record[4] = 0xFF; // 6 reserved bits set, lengthSizeMinusOne = 3 (4-byte prefixes)
        record[5] = 0xE1; // 3 reserved bits set, one SPS
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(6), (ushort)Sps.Length);
        Sps.CopyTo(record, 8);

        var offset = 8 + Sps.Length;
        record[offset++] = 1; // one PPS
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(offset), (ushort)Pps.Length);
        Pps.CopyTo(record, offset + 2);
        return record;
    }

    /// <summary>
    /// The parameter sets as two length-prefixed NAL units, ready to be spliced
    /// in front of a keyframe so that keyframe becomes a self-contained entry
    /// point for a decoder that joined late.
    /// </summary>
    public byte[] ToAvccParameterSets()
    {
        var bytes = new byte[8 + Sps.Length + Pps.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)Sps.Length);
        Sps.CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 + Sps.Length), (uint)Pps.Length);
        Pps.CopyTo(bytes, 8 + Sps.Length);
        return bytes;
    }

    /// <summary>
    /// The parameter sets as Annex-B (start-code prefixed) bytes, which is what
    /// file muxers and ffmpeg want in front of an elementary stream.
    /// </summary>
    public byte[] ToAnnexB()
    {
        var bytes = new byte[8 + Sps.Length + Pps.Length];
        bytes[3] = 1;
        Sps.CopyTo(bytes, 4);
        bytes[4 + Sps.Length + 3] = 1;
        Pps.CopyTo(bytes, 8 + Sps.Length);
        return bytes;
    }
}
