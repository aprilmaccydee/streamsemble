// Apple Lossless (ALAC) decoder — C# port of the decode path of Apple's
// open-source ALAC codec (https://github.com/macosforge/alac), which is
// licensed under the Apache License, Version 2.0:
//
//   Copyright (c) 2011 Apple Inc. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Streamsemble.AirPlay.Receiver.Audio;

/// <summary>
/// Apple Lossless (ALAC) frame decoder for AirPlay receivers.
///
/// <para>
/// This is a C# port of the decode path of Apple's open-source ALAC codec
/// (github.com/macosforge/alac, Apache-2.0). Ported pieces:
/// <c>ALACDecoder.cpp</c> (magic-cookie parsing incl. 'frma'/'alac' atom
/// skipping, frame syntax: SCE/LFE/CPE/DSE/FIL/END elements, partial-frame
/// and escape/verbatim handling), <c>ag_dec.c</c> (adaptive Golomb/Rice
/// entropy decode: <c>dyn_decomp</c>, <c>dyn_get</c>, <c>dyn_get_32bit</c>),
/// <c>dp_dec.c</c> (adaptive FIR predictor: <c>unpc_block</c>),
/// <c>matrix_dec.c</c> (<c>unmix16</c> stereo un-mixing) and
/// <c>ALACBitUtilities.c</c> (MSB-first bit reader).
/// </para>
///
/// <para>
/// Scope: 16-bit samples, 1 or 2 channels (this covers macOS AirPlay realtime
/// streams: 44100 Hz / 16-bit / 2ch / 352 samples per packet). The 20/24/32-bit
/// output paths and >2-channel layouts of the original were not ported; the
/// per-element "shift-off" (<c>bytesShifted</c>) side-channel is parsed and
/// skipped because it never contributes to 16-bit output. Output is
/// interleaved signed 16-bit little-endian PCM.
/// </para>
///
/// <para>
/// One packet == one ALAC frame (as carried in RTP by AirPlay, or as one CAF
/// / MP4 packet). Frames are independent, so packet loss does not poison the
/// decoder. Instances are not thread-safe. Decoding performs no per-packet
/// heap allocations once the internal work buffer has grown to the largest
/// packet size seen.
/// </para>
/// </summary>
public sealed class AlacDecoder
{
    // --- constants from aglib.h ---------------------------------------------
    private const int QBShift = 9;
    private const uint QB = 1u << QBShift;
    private const int MMulShift = 2;
    private const int MDenShift = QBShift - MMulShift - 1;  // 6
    private const uint MOff = 1u << (MDenShift - 2);        // 16
    private const int BitOff = 24;
    private const uint MaxPrefix16 = 9;
    private const int MaxDatatypeBits16 = 16;
    private const uint MaxPrefix32 = 9;
    private const uint NMaxMeanClamp = 0xffff;              // ag_dec.c
    private const uint NMeanClampVal = 0xffff;

    // Element tags (ALACBitUtilities.h).
    private const int IdSce = 0;  // single channel element
    private const int IdCpe = 1;  // channel pair element
    private const int IdCce = 2;  // coupling channel element (unsupported)
    private const int IdLfe = 3;  // LFE channel element (decoded like SCE)
    private const int IdDse = 4;  // data stream element (skipped)
    private const int IdPce = 5;  // (unsupported)
    private const int IdFil = 6;  // fill element (skipped)
    private const int IdEnd = 7;  // frame end

    // Zero padding appended to the packet copy so that the 32-bit loads used
    // by the Golomb decoder (which can look a few bytes past the last valid
    // bit, exactly like Apple's C code does) always stay inside the array.
    private const int PadBytes = 16;

    // --- ALACSpecificConfig --------------------------------------------------
    /// <summary>Samples per full frame (352 for AirPlay realtime, 4096 default elsewhere).</summary>
    public int FrameLength { get; }
    /// <summary>Bits per sample; only 16 is supported by this port.</summary>
    public int BitDepth { get; }
    /// <summary>Channel count (1 or 2).</summary>
    public int Channels { get; }
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; }
    /// <summary>Upper bound of <see cref="DecodePacket"/> output, in bytes.</summary>
    public int MaxBytesPerPacket => FrameLength * Channels * sizeof(short);

    private readonly uint _pb; // rice modifier ("history multiplier")
    private readonly uint _mb; // rice initial history
    private readonly uint _kb; // rice parameter limit
    private readonly uint _wb; // (1 << kb) - 1

    // --- reusable per-instance buffers (no per-packet allocation) ------------
    private readonly int[] _mixBufferU;
    private readonly int[] _mixBufferV;
    private readonly int[] _predictor;
    private readonly short[] _coefsU = new short[32];
    private readonly short[] _coefsV = new short[32];
    private byte[] _work = [];

    /// <summary>
    /// Creates a decoder from an ALAC magic cookie. Accepts the raw 24-byte
    /// ALACSpecificConfig, the 36-byte form wrapped in an 'alac' atom, and the
    /// 48-byte form wrapped in 'frma' + 'alac' atoms (each atom header is 12
    /// bytes and is skipped when bytes 4..8 spell the atom name, exactly as
    /// ALACDecoder::Init does). Trailing channel-layout info is ignored.
    /// </summary>
    /// <exception cref="ArgumentException">The cookie is malformed or too short.</exception>
    /// <exception cref="NotSupportedException">Bit depth is not 16 or channel count is not 1 or 2.</exception>
    public AlacDecoder(ReadOnlySpan<byte> magicCookie)
    {
        ReadOnlySpan<byte> config = magicCookie;

        // Skip format ('frma') atom if present, then 'alac' atom if present
        // (ALACDecoder::Init / ALACMagicCookieDescription.txt).
        if (config.Length >= 12 && config[4] == (byte)'f' && config[5] == (byte)'r' && config[6] == (byte)'m' && config[7] == (byte)'a')
        {
            config = config[12..];
        }

        if (config.Length >= 12 && config[4] == (byte)'a' && config[5] == (byte)'l' && config[6] == (byte)'a' && config[7] == (byte)'c')
        {
            config = config[12..];
        }

        if (config.Length < 24)
        {
            throw new ArgumentException($"ALAC magic cookie too short ({magicCookie.Length} bytes)", nameof(magicCookie));
        }

        FrameLength = BinaryPrimitives.ReadInt32BigEndian(config);
        var compatibleVersion = config[4];
        BitDepth = config[5];
        _pb = config[6];
        _mb = config[7];
        _kb = config[8];
        _wb = (1u << (int)_kb) - 1;
        Channels = config[9];
        // config[10..12] maxRun, [12..16] maxFrameBytes, [16..20] avgBitRate: unused by the decoder.
        SampleRate = BinaryPrimitives.ReadInt32BigEndian(config[20..]);

        if (compatibleVersion > 0)
        {
            throw new ArgumentException($"unsupported ALAC compatibleVersion {compatibleVersion}", nameof(magicCookie));
        }

        if (FrameLength is < 1 or > (1 << 20) || SampleRate < 1 || _kb > 30)
        {
            throw new ArgumentException("implausible ALACSpecificConfig", nameof(magicCookie));
        }

        if (BitDepth != 16)
        {
            throw new NotSupportedException($"only 16-bit ALAC is supported (cookie says {BitDepth}-bit)");
        }

        if (Channels is not (1 or 2))
        {
            throw new NotSupportedException($"only mono/stereo ALAC is supported (cookie says {Channels} channels)");
        }

        _mixBufferU = new int[FrameLength];
        _mixBufferV = new int[FrameLength];
        _predictor = new int[FrameLength];
    }

    /// <summary>
    /// Decodes one ALAC packet into interleaved s16le PCM.
    /// Returns the number of bytes written to <paramref name="pcmOut"/>
    /// (<see cref="MaxBytesPerPacket"/> for a full frame, less for a trailing
    /// partial frame).
    /// </summary>
    /// <exception cref="InvalidDataException">The packet is truncated or malformed.</exception>
    /// <exception cref="ArgumentException"><paramref name="pcmOut"/> is too small.</exception>
    public int DecodePacket(ReadOnlySpan<byte> packet, Span<byte> pcmOut)
    {
        if (packet.IsEmpty)
        {
            throw new InvalidDataException("empty ALAC packet");
        }

        if (_work.Length < packet.Length + PadBytes)
        {
            _work = new byte[checked(packet.Length + PadBytes)];
        }

        packet.CopyTo(_work);
        _work.AsSpan(packet.Length, PadBytes).Clear();

        try
        {
            return Decode(new BitReader(_work, packet.Length), MemoryMarshal.Cast<byte, short>(pcmOut));
        }
        catch (IndexOutOfRangeException e)
        {
            // Safety net: any bit-stream runaway that slipped past the explicit
            // bounds checks lands on the managed array bounds, never on memory
            // outside the work buffer.
            throw new InvalidDataException("malformed ALAC packet (bitstream overrun)", e);
        }
    }

    /// <summary>Port of ALACDecoder::Decode, 16-bit output paths only.</summary>
    private int Decode(BitReader bits, Span<short> pcm)
    {
        var numSamples = FrameLength;
        var outNumSamples = numSamples;
        var channelIndex = 0;

        while (true)
        {
            // bail if we ran off the end of the buffer
            if (bits.Cur >= bits.ByteSize)
            {
                throw new InvalidDataException("ALAC packet ended before END element");
            }

            var tag = bits.ReadSmall(3);
            switch (tag)
            {
                case IdSce:
                case IdLfe:
                    DecodeMonoElement(ref bits, pcm, ref numSamples, channelIndex);
                    channelIndex += 1;
                    outNumSamples = numSamples;
                    break;

                case IdCpe:
                    // if decoding this pair would take us over the channel limit, bail
                    if (channelIndex + 2 > Channels)
                    {
                        goto NoMoreChannels;
                    }

                    DecodeStereoElement(ref bits, pcm, ref numSamples, channelIndex);
                    channelIndex += 2;
                    outNumSamples = numSamples;
                    break;

                case IdDse:
                    SkipDataStreamElement(ref bits);
                    break;

                case IdFil:
                    SkipFillElement(ref bits);
                    break;

                case IdEnd:
                    bits.ByteAlign();
                    goto NoMoreChannels;

                default: // IdCce, IdPce
                    throw new InvalidDataException($"unsupported ALAC element tag {tag}");
            }

            if (channelIndex >= Channels)
            {
                break;
            }
        }

    NoMoreChannels:
        // Zero any channels the bitstream did not deliver. (Apple only does
        // this on the "too many channels requested" path; doing it for an
        // early END element as well means pcmOut is never left with stale data.)
        if (channelIndex < Channels)
        {
            RequireCapacity(pcm, outNumSamples);
        }

        for (; channelIndex < Channels; channelIndex++)
        {
            for (int i = 0, j = channelIndex; i < outNumSamples; i++, j += Channels)
            {
                pcm[j] = 0;
            }
        }

        return outNumSamples * Channels * sizeof(short);
    }

    /// <summary>SCE/LFE element (mono channel), 16-bit path of ALACDecoder::Decode.</summary>
    private void DecodeMonoElement(ref BitReader bits, Span<short> pcm, ref int numSamples, int channelIndex)
    {
        bits.ReadSmall(4); // element instance tag

        if (bits.Read(12) != 0) // 12 unused header bits, must be zero
        {
            throw new InvalidDataException("nonzero unused-header bits in ALAC SCE");
        }

        // 1-bit partial-frame flag, 2-bit shift-off, 1-bit escape flag
        var headerByte = bits.Read(4);
        var partialFrame = headerByte >> 3;
        var bytesShifted = (headerByte >> 1) & 3;
        if (bytesShifted == 3)
        {
            throw new InvalidDataException("invalid ALAC shift-off value 3");
        }

        var escapeFlag = headerByte & 1;
        var chanBits = (uint)(BitDepth - bytesShifted * 8);

        if (partialFrame != 0)
        {
            numSamples = ReadPartialFrameLength(ref bits);
        }

        RequireCapacity(pcm, numSamples);

        if (escapeFlag == 0)
        {
            // compressed frame, read rest of parameters
            bits.Read(8); // mixBits — always 0 for mono
            bits.Read(8); // mixRes — always 0 for mono

            headerByte = bits.Read(8);
            var modeU = headerByte >> 4;
            var denShiftU = (uint)(headerByte & 0xf);

            headerByte = bits.Read(8);
            var pbFactorU = (uint)(headerByte >> 5);
            var numU = headerByte & 0x1f;
            for (var i = 0; i < numU; i++)
            {
                _coefsU[i] = (short)bits.Read(16);
            }

            // if shift active, skip the shift buffer (its contents never reach 16-bit output)
            if (bytesShifted != 0)
            {
                bits.Advance((long)(bytesShifted * 8) * numSamples);
            }

            DynDecomp(_pb * pbFactorU / 4, ref bits, _predictor, numSamples, (int)chanBits);

            if (modeU == 0)
            {
                UnpcBlock(_predictor, _mixBufferU, numSamples, _coefsU, numU, chanBits, denShiftU);
            }
            else
            {
                // the special "numActive == 31" mode can be done in-place
                UnpcBlock(_predictor, _predictor, numSamples, null, 31, chanBits, 0);
                UnpcBlock(_predictor, _mixBufferU, numSamples, _coefsU, numU, chanBits, denShiftU);
            }
        }
        else
        {
            // uncompressed (verbatim) frame; chanBits <= 16 always holds here
            var shift = 32 - (int)chanBits;
            for (var i = 0; i < numSamples; i++)
            {
                var val = bits.Read((int)chanBits);
                _mixBufferU[i] = (val << shift) >> shift;
            }
        }

        // convert 32-bit integers into the output buffer
        for (int i = 0, j = channelIndex; i < numSamples; i++, j += Channels)
        {
            pcm[j] = (short)_mixBufferU[i];
        }
    }

    /// <summary>CPE element (stereo pair), 16-bit path of ALACDecoder::Decode.</summary>
    private void DecodeStereoElement(ref BitReader bits, Span<short> pcm, ref int numSamples, int channelIndex)
    {
        bits.ReadSmall(4); // element instance tag

        if (bits.Read(12) != 0) // 12 unused header bits, must be zero
        {
            throw new InvalidDataException("nonzero unused-header bits in ALAC CPE");
        }

        // 1-bit partial-frame flag, 2-bit shift-off, 1-bit escape flag
        var headerByte = bits.Read(4);
        var partialFrame = headerByte >> 3;
        var bytesShifted = (headerByte >> 1) & 3;
        if (bytesShifted == 3)
        {
            throw new InvalidDataException("invalid ALAC shift-off value 3");
        }

        var escapeFlag = headerByte & 1;
        var chanBits = (uint)(BitDepth - bytesShifted * 8 + 1);

        if (partialFrame != 0)
        {
            numSamples = ReadPartialFrameLength(ref bits);
        }

        RequireCapacity(pcm, numSamples);

        int mixBits;
        int mixRes;
        if (escapeFlag == 0)
        {
            // compressed frame, read rest of parameters
            mixBits = bits.Read(8);
            mixRes = (sbyte)bits.Read(8);

            headerByte = bits.Read(8);
            var modeU = headerByte >> 4;
            var denShiftU = (uint)(headerByte & 0xf);

            headerByte = bits.Read(8);
            var pbFactorU = (uint)(headerByte >> 5);
            var numU = headerByte & 0x1f;
            for (var i = 0; i < numU; i++)
            {
                _coefsU[i] = (short)bits.Read(16);
            }

            headerByte = bits.Read(8);
            var modeV = headerByte >> 4;
            var denShiftV = (uint)(headerByte & 0xf);

            headerByte = bits.Read(8);
            var pbFactorV = (uint)(headerByte >> 5);
            var numV = headerByte & 0x1f;
            for (var i = 0; i < numV; i++)
            {
                _coefsV[i] = (short)bits.Read(16);
            }

            // if shift active, skip the interleaved shift buffer (never reaches 16-bit output)
            if (bytesShifted != 0)
            {
                bits.Advance((long)(bytesShifted * 8) * 2 * numSamples);
            }

            // decompress and run predictor for "left" channel
            DynDecomp(_pb * pbFactorU / 4, ref bits, _predictor, numSamples, (int)chanBits);
            if (modeU == 0)
            {
                UnpcBlock(_predictor, _mixBufferU, numSamples, _coefsU, numU, chanBits, denShiftU);
            }
            else
            {
                UnpcBlock(_predictor, _predictor, numSamples, null, 31, chanBits, 0);
                UnpcBlock(_predictor, _mixBufferU, numSamples, _coefsU, numU, chanBits, denShiftU);
            }

            // decompress and run predictor for "right" channel
            DynDecomp(_pb * pbFactorV / 4, ref bits, _predictor, numSamples, (int)chanBits);
            if (modeV == 0)
            {
                UnpcBlock(_predictor, _mixBufferV, numSamples, _coefsV, numV, chanBits, denShiftV);
            }
            else
            {
                UnpcBlock(_predictor, _predictor, numSamples, null, 31, chanBits, 0);
                UnpcBlock(_predictor, _mixBufferV, numSamples, _coefsV, numV, chanBits, denShiftV);
            }
        }
        else
        {
            // uncompressed (escape) frame — this is what RAOP "verbatim" senders emit.
            // Samples are stored interleaved L,R at the full bit depth.
            chanBits = (uint)BitDepth;
            var shift = 32 - (int)chanBits;
            for (var i = 0; i < numSamples; i++)
            {
                var val = bits.Read((int)chanBits);
                _mixBufferU[i] = (val << shift) >> shift;

                val = bits.Read((int)chanBits);
                _mixBufferV[i] = (val << shift) >> shift;
            }

            mixBits = 0;
            mixRes = 0;
        }

        // un-mix the data and convert to output format
        // (mixRes == 0 means plain L/R, so the escape path shares this code)
        Unmix16(_mixBufferU, _mixBufferV, pcm, channelIndex, Channels, numSamples, mixBits, mixRes);
    }

    private int ReadPartialFrameLength(ref BitReader bits)
    {
        var numSamples = bits.Read(16) << 16;
        numSamples |= bits.Read(16);
        if ((uint)numSamples > (uint)FrameLength)
        {
            // (Apple trusts this field; we bound it to the buffers we allocated.)
            throw new InvalidDataException($"ALAC partial frame length {numSamples} exceeds frameLength {FrameLength}");
        }

        return numSamples;
    }

    private void RequireCapacity(Span<short> pcm, int numSamples)
    {
        if ((long)numSamples * Channels > pcm.Length)
        {
            throw new ArgumentException($"pcmOut too small: need {numSamples * Channels * sizeof(short)} bytes");
        }
    }

    /// <summary>Port of DataStreamElement() — parse but ignore.</summary>
    private void SkipDataStreamElement(ref BitReader bits)
    {
        bits.ReadSmall(4); // element instance tag
        var dataByteAlignFlag = bits.ReadOne();

        // 8-bit count or (8-bit + 8-bit count) if 8-bit count == 255
        int count = bits.ReadSmall(8);
        if (count == 255)
        {
            count += bits.ReadSmall(8);
        }

        if (dataByteAlignFlag != 0)
        {
            bits.ByteAlign();
        }

        bits.Advance(count * 8L);
        if (bits.Cur > bits.ByteSize)
        {
            throw new InvalidDataException("ALAC data stream element overruns packet");
        }
    }

    /// <summary>Port of FillElement() — parse but ignore.</summary>
    private void SkipFillElement(ref BitReader bits)
    {
        // 4-bit count or (4-bit + 8-bit count) if 4-bit count == 15
        int count = bits.ReadSmall(4);
        if (count == 15)
        {
            count += bits.ReadSmall(8) - 1;
        }

        bits.Advance(count * 8L);
        if (bits.Cur > bits.ByteSize)
        {
            throw new InvalidDataException("ALAC fill element overruns packet");
        }
    }

    // -------------------------------------------------------------------------
    // Adaptive Golomb/Rice decode — port of ag_dec.c
    // -------------------------------------------------------------------------

    /// <summary>
    /// Port of dyn_decomp(): decodes <paramref name="numSamples"/> prediction
    /// residuals into <paramref name="pc"/>. <paramref name="pb"/> is the
    /// per-element rice modifier (config pb scaled by the pbFactor); kb/wb and
    /// the initial history mb come from the config.
    /// </summary>
    private void DynDecomp(uint pb, ref BitReader bits, int[] pc, int numSamples, int maxSize)
    {
        var buf = _work;
        var bitPos = (uint)bits.BitPosition;
        var startPos = bitPos;
        // Note: Apple computes the overrun limit from the whole buffer size
        // relative to the current read pointer, which over-allows by however
        // many bits were already consumed; using the packet's absolute bit
        // count is strictly tighter.
        var maxPos = (uint)bits.ByteSize * 8;

        var mb = _mb; // mb0
        var zmode = 0u;
        var c = 0;

        while (c < numSamples)
        {
            // bail if we've run off the end of the buffer
            if (bitPos >= maxPos)
            {
                throw new InvalidDataException("ALAC entropy stream overruns packet");
            }

            var m = mb >> QBShift;
            var k = Lg3a(m);
            k = Math.Min(k, (int)_kb);
            m = (1u << k) - 1;

            var n = DynGet32Bit(buf, ref bitPos, m, k, maxSize);

            // least significant bit is sign bit
            {
                var ndecode = n + zmode;
                var multiplier = -(int)(ndecode & 1) | 1;
                pc[c] = unchecked((int)((ndecode + 1) >> 1) * multiplier);
            }

            c++;

            mb = unchecked(pb * (n + zmode) + mb - ((pb * mb) >> QBShift));

            // update mean tracking
            if (n > NMaxMeanClamp)
            {
                mb = NMeanClampVal;
            }

            zmode = 0;

            if (((mb << MMulShift) < QB) && (c < numSamples))
            {
                zmode = 1;
                var kz = LeadingZeros(mb) - BitOff + (int)((mb + MOff) >> MDenShift);
                var mz = ((1u << kz) - 1) & _wb;

                n = DynGet(buf, ref bitPos, mz, kz);

                if ((long)c + n > numSamples)
                {
                    throw new InvalidDataException("ALAC zero-run overruns frame");
                }

                for (var j = 0u; j < n; j++)
                {
                    pc[c] = 0;
                    c++;
                }

                if (n >= 65535)
                {
                    zmode = 0;
                }

                mb = 0;
            }
        }

        bits.Advance(bitPos - startPos);
        if (bits.Cur > bits.ByteSize)
        {
            throw new InvalidDataException("ALAC entropy stream overruns packet");
        }
    }

    /// <summary>Port of dyn_get(): modified Golomb decode for zero-run lengths.</summary>
    private static uint DynGet(byte[] buf, ref uint bitPos, uint m, int k)
    {
        var tempBits = bitPos;
        var streamlong = Read32BE(buf, (int)(tempBits >> 3));
        streamlong <<= (int)(tempBits & 7);

        // find the number of bits in the prefix
        var pre = (uint)LeadingZeros(~streamlong);

        uint result;
        if (pre >= MaxPrefix16)
        {
            pre = MaxPrefix16;
            tempBits += pre;
            streamlong <<= (int)pre;
            result = GetNextFromLong(streamlong, MaxDatatypeBits16);
            tempBits += MaxDatatypeBits16;
        }
        else
        {
            tempBits += pre + 1;
            streamlong <<= (int)(pre + 1);
            var v = GetNextFromLong(streamlong, k);
            tempBits += (uint)k;

            result = unchecked(pre * m + v - 1);
            if (v < 2)
            {
                result -= unchecked(v - 1);
                tempBits -= 1;
            }
        }

        bitPos = tempBits;
        return result;
    }

    /// <summary>Port of dyn_get_32bit(): modified Golomb decode for residuals.</summary>
    private static uint DynGet32Bit(byte[] buf, ref uint bitPos, uint m, int k, int maxBits)
    {
        var tempBits = bitPos;
        var streamlong = Read32BE(buf, (int)(tempBits >> 3));
        streamlong <<= (int)(tempBits & 7);

        // find the number of bits in the prefix
        var result = (uint)LeadingZeros(~streamlong);

        if (result >= MaxPrefix32)
        {
            result = GetStreamBits(buf, tempBits + MaxPrefix32, maxBits);
            tempBits += MaxPrefix32 + (uint)maxBits;
        }
        else
        {
            tempBits += result + 1;

            if (k != 1)
            {
                streamlong <<= (int)(result + 1);
                var v = GetNextFromLong(streamlong, k);
                tempBits += (uint)k;
                tempBits -= 1;
                result *= m;

                if (v >= 2)
                {
                    result += v - 1;
                    tempBits += 1;
                }
            }
        }

        bitPos = tempBits;
        return result;
    }

    /// <summary>Port of getstreambits(): reads up to 32 bits at an absolute bit offset.</summary>
    private static uint GetStreamBits(byte[] buf, uint bitOffset, int numBits)
    {
        var byteOffset = (int)(bitOffset >> 3);
        var bitShift = (int)(bitOffset & 7);
        var load1 = Read32BE(buf, byteOffset);

        uint result;
        if (numBits + bitShift > 32)
        {
            result = load1 << bitShift;
            uint load2 = buf[byteOffset + 4];
            load2 >>= 8 - (numBits + bitShift - 32);
            result >>= 32 - numBits;
            result |= load2;
        }
        else
        {
            result = load1 >> (32 - numBits - bitShift);
        }

        if (numBits != 32)
        {
            result &= ~(0xffffffffu << numBits);
        }

        return result;
    }

    /// <summary>get_next_fromlong(): top <paramref name="suff"/> bits of the loaded word.</summary>
    private static uint GetNextFromLong(uint inLong, int suff) => inLong >> (32 - suff);

    /// <summary>lead(): number of leading zero bits (32 for zero) — matches Apple's lead().</summary>
    private static int LeadingZeros(uint value) => BitOperations.LeadingZeroCount(value);

    /// <summary>lg3a(): ceil(log2(x + 3)) - 1, used to derive the rice parameter k.</summary>
    private static int Lg3a(uint x) => 31 - LeadingZeros(x + 3);

    private static uint Read32BE(byte[] buf, int offset) =>
        ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16) | ((uint)buf[offset + 2] << 8) | buf[offset + 3];

    // -------------------------------------------------------------------------
    // Dynamic (adaptive FIR) predictor — port of dp_dec.c
    // -------------------------------------------------------------------------

    /// <summary>sign_of_int()</summary>
    private static int SignOfInt(int i) => (int)((uint)(-i) >> 31) | (i >> 31);

    /// <summary>
    /// Port of unpc_block(): runs the adaptive FIR predictor over the residuals
    /// in <paramref name="pc1"/>, producing samples in <paramref name="output"/>.
    /// The coefficients adapt in place, exactly as in the original (the C file's
    /// unrolled numactive == 4/8 variants compute the same values as the general
    /// loop, so only the general loop was ported).
    /// </summary>
    private static void UnpcBlock(int[] pc1, int[] output, int num, short[]? coefs, int numActive, uint chanBits, uint denShift)
    {
        var chanShift = 32 - (int)chanBits;
        var denHalf = denShift > 0 ? 1 << ((int)denShift - 1) : 0;

        output[0] = pc1[0];

        if (numActive == 0)
        {
            // just copy if numActive == 0 (but don't bother if in/out are the same)
            if (num > 1 && !ReferenceEquals(pc1, output))
            {
                Array.Copy(pc1, 1, output, 1, num - 1);
            }

            return;
        }

        if (numActive == 31)
        {
            // short-circuit if numActive == 31 (used as a first pass, can run in place)
            var prev = output[0];
            for (var j = 1; j < num; j++)
            {
                var del31 = pc1[j] + prev;
                prev = (del31 << chanShift) >> chanShift;
                output[j] = prev;
            }

            return;
        }

        for (var j = 1; j <= numActive; j++)
        {
            var del = pc1[j] + output[j - 1];
            output[j] = (del << chanShift) >> chanShift;
        }

        var lim = numActive + 1;
        for (var j = lim; j < num; j++)
        {
            var sum1 = 0;
            var top = output[j - lim];
            var pout = j - 1;
            for (var k = 0; k < numActive; k++)
            {
                sum1 = unchecked(sum1 + coefs![k] * (output[pout - k] - top));
            }

            var del = pc1[j];
            var del0 = del;
            var sg = SignOfInt(del);
            del = unchecked(del + top + ((sum1 + denHalf) >> (int)denShift));
            output[j] = (del << chanShift) >> chanShift;

            if (sg > 0)
            {
                for (var k = numActive - 1; k >= 0; k--)
                {
                    var dd = top - output[pout - k];
                    var sgn = SignOfInt(dd);
                    coefs![k] = (short)(coefs[k] - sgn);
                    del0 -= (numActive - k) * ((sgn * dd) >> (int)denShift);
                    if (del0 <= 0)
                    {
                        break;
                    }
                }
            }
            else if (sg < 0)
            {
                for (var k = numActive - 1; k >= 0; k--)
                {
                    var dd = top - output[pout - k];
                    var sgn = SignOfInt(dd);
                    coefs![k] = (short)(coefs[k] + sgn);
                    del0 -= (numActive - k) * ((-sgn * dd) >> (int)denShift);
                    if (del0 >= 0)
                    {
                        break;
                    }
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Stereo un-mixing — port of matrix_dec.c unmix16()
    // -------------------------------------------------------------------------

    private static void Unmix16(int[] u, int[] v, Span<short> output, int outOffset, int stride, int numSamples, int mixBits, int mixRes)
    {
        var op = outOffset;
        if (mixRes != 0)
        {
            // matrixed stereo
            for (var j = 0; j < numSamples; j++)
            {
                var l = u[j] + v[j] - ((mixRes * v[j]) >> mixBits);
                var r = l - v[j];
                output[op] = (short)l;
                output[op + 1] = (short)r;
                op += stride;
            }
        }
        else
        {
            // conventional separated stereo
            for (var j = 0; j < numSamples; j++)
            {
                output[op] = (short)u[j];
                output[op + 1] = (short)v[j];
                op += stride;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Bit reader — port of ALACBitUtilities.c (read side only)
    // -------------------------------------------------------------------------

    /// <summary>
    /// MSB-first bit reader over the zero-padded packet copy. Semantics match
    /// BitBufferRead / BitBufferReadSmall / BitBufferReadOne: each read may
    /// touch up to 2 bytes beyond the current byte, which the padding absorbs;
    /// a read that *starts* past the logical end throws.
    /// </summary>
    private struct BitReader(byte[] buffer, int byteSize)
    {
        private readonly byte[] _buf = buffer;

        /// <summary>Current byte index.</summary>
        public int Cur { get; private set; }

        /// <summary>Bit position within the current byte (0..7).</summary>
        private int _bitIndex;

        /// <summary>Logical packet length in bytes (excludes padding).</summary>
        public int ByteSize { get; } = byteSize;

        /// <summary>Absolute position in bits from the start of the packet.</summary>
        public readonly long BitPosition => (long)Cur * 8 + _bitIndex;

        /// <summary>BitBufferRead(): reads 1..16 bits.</summary>
        public int Read(int numBits)
        {
            if (Cur > ByteSize)
            {
                ThrowPastEnd();
            }

            var v = ((uint)_buf[Cur] << 16) | ((uint)_buf[Cur + 1] << 8) | _buf[Cur + 2];
            v = (v << _bitIndex) & 0x00FFFFFF;
            _bitIndex += numBits;
            v >>= 24 - numBits;
            Cur += _bitIndex >> 3;
            _bitIndex &= 7;
            return (int)v;
        }

        /// <summary>BitBufferReadSmall(): reads 1..8 bits.</summary>
        public int ReadSmall(int numBits)
        {
            if (Cur > ByteSize)
            {
                ThrowPastEnd();
            }

            var v = (uint)((_buf[Cur] << 8) | _buf[Cur + 1]);
            v = (v << _bitIndex) & 0xFFFF;
            _bitIndex += numBits;
            v >>= 16 - numBits;
            Cur += _bitIndex >> 3;
            _bitIndex &= 7;
            return (int)v;
        }

        /// <summary>BitBufferReadOne(): reads a single bit.</summary>
        public int ReadOne()
        {
            if (Cur > ByteSize)
            {
                ThrowPastEnd();
            }

            var r = (_buf[Cur] >> (7 - _bitIndex)) & 1;
            _bitIndex++;
            Cur += _bitIndex >> 3;
            _bitIndex &= 7;
            return r;
        }

        /// <summary>BitBufferAdvance().</summary>
        public void Advance(long numBits)
        {
            var position = BitPosition + numBits;
            Cur = (int)(position >> 3);
            _bitIndex = (int)(position & 7);
        }

        /// <summary>BitBufferByteAlign(read side).</summary>
        public void ByteAlign()
        {
            if (_bitIndex != 0)
            {
                Cur++;
                _bitIndex = 0;
            }
        }

        private static void ThrowPastEnd() =>
            throw new InvalidDataException("ALAC packet truncated (read past end)");
    }
}
