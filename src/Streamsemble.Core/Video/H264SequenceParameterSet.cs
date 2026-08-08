namespace Streamsemble.Core.Video;

/// <summary>
/// Just enough of the H.264 sequence parameter set to recover the picture size.
/// The mirror protocol never states the resolution anywhere — a receiver learns
/// it by parsing the SPS the sender sends in its codec-config packet — so this
/// is the only way to say what is actually on screen.
/// </summary>
public static class H264SequenceParameterSet
{
    /// <summary>
    /// Profiles whose SPS carries the extra chroma/scaling-matrix prologue.
    /// A set rather than an array on purpose: the membership test used to be
    /// Array.IndexOf against a uint, which binds to the non-generic object
    /// overload, boxes, and never matches — so High profile silently skipped
    /// the prologue and every dimension after it was read off a misaligned
    /// cursor. macOS mirrors in High, so that was every real stream.
    /// </summary>
    private static readonly HashSet<int> HighProfiles =
        [100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135];

    /// <summary>
    /// Decoded (width, height) in luma samples, cropping applied. Returns
    /// (0, 0) rather than throwing when the SPS is truncated or uses syntax we
    /// do not walk: an unreadable size is a display detail, never a reason to
    /// drop a stream that decodes perfectly well.
    /// </summary>
    public static (int Width, int Height) PictureSize(ReadOnlySpan<byte> sps)
    {
        try
        {
            return Decode(sps);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return (0, 0);
        }
    }

    private static (int Width, int Height) Decode(ReadOnlySpan<byte> sps)
    {
        // Skip the NAL header byte; the rest is the RBSP once the emulation
        // prevention bytes are removed.
        if (sps.Length < 4)
        {
            throw new InvalidDataException("SPS too short");
        }

        var rbsp = RemoveEmulationPrevention(sps[1..]);
        var reader = new BitReader(rbsp);

        var profileIdc = (int)reader.ReadBits(8);
        reader.ReadBits(8);  // constraint flags + reserved
        reader.ReadBits(8);  // level_idc
        reader.ReadUnsigned(); // seq_parameter_set_id

        var chromaFormatIdc = 1; // 4:2:0 unless the high-profile prologue says otherwise
        var separateColourPlane = false;
        if (HighProfiles.Contains(profileIdc))
        {
            chromaFormatIdc = (int)reader.ReadUnsigned();
            if (chromaFormatIdc == 3)
            {
                separateColourPlane = reader.ReadBit();
            }

            reader.ReadUnsigned(); // bit_depth_luma_minus8
            reader.ReadUnsigned(); // bit_depth_chroma_minus8
            reader.ReadBit();      // qpprime_y_zero_transform_bypass_flag
            if (reader.ReadBit())  // seq_scaling_matrix_present_flag
            {
                var lists = chromaFormatIdc != 3 ? 8 : 12;
                for (var i = 0; i < lists; i++)
                {
                    if (reader.ReadBit())
                    {
                        SkipScalingList(ref reader, i < 6 ? 16 : 64);
                    }
                }
            }
        }

        reader.ReadUnsigned(); // log2_max_frame_num_minus4
        var picOrderCntType = reader.ReadUnsigned();
        if (picOrderCntType == 0)
        {
            reader.ReadUnsigned(); // log2_max_pic_order_cnt_lsb_minus4
        }
        else if (picOrderCntType == 1)
        {
            reader.ReadBit();      // delta_pic_order_always_zero_flag
            reader.ReadSigned();   // offset_for_non_ref_pic
            reader.ReadSigned();   // offset_for_top_to_bottom_field
            var cycle = reader.ReadUnsigned();
            for (var i = 0UL; i < cycle; i++)
            {
                reader.ReadSigned();
            }
        }

        reader.ReadUnsigned(); // max_num_ref_frames
        reader.ReadBit();      // gaps_in_frame_num_value_allowed_flag

        var widthInMbs = (int)reader.ReadUnsigned() + 1;
        var heightInMapUnits = (int)reader.ReadUnsigned() + 1;
        var frameMbsOnly = reader.ReadBit();
        if (!frameMbsOnly)
        {
            reader.ReadBit(); // mb_adaptive_frame_field_flag
        }

        reader.ReadBit(); // direct_8x8_inference_flag

        int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
        if (reader.ReadBit()) // frame_cropping_flag
        {
            cropLeft = (int)reader.ReadUnsigned();
            cropRight = (int)reader.ReadUnsigned();
            cropTop = (int)reader.ReadUnsigned();
            cropBottom = (int)reader.ReadUnsigned();
        }

        // Crop offsets are in chroma sample units, so they scale by the chroma
        // subsampling — 2x2 for the 4:2:0 every AirPlay mirror stream uses.
        var subWidth = chromaFormatIdc is 1 or 2 && !separateColourPlane ? 2 : 1;
        var subHeight = chromaFormatIdc == 1 && !separateColourPlane ? 2 : 1;
        var frameHeightMultiplier = frameMbsOnly ? 1 : 2;

        var width = widthInMbs * 16 - subWidth * (cropLeft + cropRight);
        var height = frameHeightMultiplier * heightInMapUnits * 16
                     - subHeight * frameHeightMultiplier * (cropTop + cropBottom);
        return (width, height);
    }

    private static void SkipScalingList(ref BitReader reader, int size)
    {
        var lastScale = 8;
        var nextScale = 8;
        for (var i = 0; i < size; i++)
        {
            if (nextScale != 0)
            {
                var delta = reader.ReadSigned();
                nextScale = (lastScale + (int)delta + 256) % 256;
            }

            lastScale = nextScale == 0 ? lastScale : nextScale;
        }
    }

    /// <summary>Strips the 0x03 bytes an encoder inserts to keep 00 00 01 out of the payload.</summary>
    internal static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> nal)
    {
        var output = new byte[nal.Length];
        var written = 0;
        var zeros = 0;
        foreach (var b in nal)
        {
            if (zeros >= 2 && b == 0x03)
            {
                zeros = 0;
                continue;
            }

            output[written++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return output[..written];
    }

    /// <summary>Big-endian bit cursor with the exp-Golomb reads the SPS syntax is written in.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bit;

        public bool ReadBit()
        {
            if (_bit >= _data.Length * 8)
            {
                throw new InvalidDataException("SPS ended mid-syntax");
            }

            var value = (_data[_bit >> 3] >> (7 - (_bit & 7))) & 1;
            _bit++;
            return value != 0;
        }

        public uint ReadBits(int count)
        {
            var value = 0u;
            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | (ReadBit() ? 1u : 0u);
            }

            return value;
        }

        /// <summary>Unsigned exp-Golomb: N leading zeros, a 1, then N more bits.</summary>
        public ulong ReadUnsigned()
        {
            var leadingZeros = 0;
            while (!ReadBit())
            {
                if (++leadingZeros > 32)
                {
                    throw new InvalidDataException("exp-Golomb code longer than 32 bits");
                }
            }

            return leadingZeros == 0 ? 0 : (1UL << leadingZeros) - 1 + ReadBits(leadingZeros);
        }

        /// <summary>Signed exp-Golomb: the unsigned code zig-zagged around zero.</summary>
        public long ReadSigned()
        {
            var code = ReadUnsigned();
            var magnitude = (long)((code + 1) / 2);
            return (code & 1) != 0 ? magnitude : -magnitude;
        }
    }
}
