using System.Buffers.Binary;

namespace Streamsemble.AirPlay.Common.Video;

/// <summary>What the payload behind a mirror packet header is.</summary>
public enum MirrorPayloadType : byte
{
    /// <summary>One H.264 access unit, AVCC framed and encrypted.</summary>
    Video = 0,

    /// <summary>An avcC decoder-configuration record. Sent in the CLEAR — the only payload that is.</summary>
    CodecConfig = 1,

    /// <summary>Empty keep-alive; senders emit one every few seconds while idle.</summary>
    Heartbeat = 2,
}

/// <summary>
/// The 128-byte header in front of every packet on an AirPlay mirroring data
/// channel. Only the first 16 bytes carry meaning — length, type, an option
/// word, and the presentation timestamp — and the rest is sender-specific
/// padding that receivers ignore, so we parse the 16 and preserve the shape.
/// Little-endian throughout, unlike the big-endian RTP the audio path uses.
/// </summary>
/// <param name="PayloadLength">Bytes of payload following the header.</param>
/// <param name="PayloadType">Video, codec config, or heartbeat.</param>
/// <param name="PayloadOption">
/// Sender-defined flags. Not interpreted; forwarded verbatim so a passthrough
/// output stream is byte-identical to what arrived.
/// </param>
/// <param name="TimestampNtp">
/// When the frame should be presented, in 64-bit NTP format on the SENDER's
/// timing clock — the clock its <c>timingPort</c> serves, not ours. Meaningless
/// until translated through that clock's offset.
/// </param>
public readonly record struct MirrorPacketHeader(
    int PayloadLength,
    MirrorPayloadType PayloadType,
    ushort PayloadOption,
    ulong TimestampNtp)
{
    /// <summary>Fixed header size on the wire.</summary>
    public const int Length = 128;

    /// <summary>
    /// Sanity bound on a single access unit. A 4K keyframe runs a few hundred
    /// kilobytes; anything past this means the stream desynchronized and we are
    /// reading padding as a length field.
    /// </summary>
    public const int MaxPayloadLength = 8 * 1024 * 1024;

    public static MirrorPacketHeader Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length < Length)
        {
            throw new ArgumentException($"mirror header must be {Length} bytes, got {header.Length}", nameof(header));
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength is < 0 or > MaxPayloadLength)
        {
            throw new InvalidDataException(
                $"mirror packet claims a {payloadLength} B payload — the data channel is out of frame");
        }

        return new MirrorPacketHeader(
            payloadLength,
            (MirrorPayloadType)(byte)BinaryPrimitives.ReadUInt16LittleEndian(header[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[8..]));
    }

    /// <summary>Serializes to a fresh 128-byte header; the trailing 112 bytes stay zero.</summary>
    public byte[] ToBytes()
    {
        var header = new byte[Length];
        BinaryPrimitives.WriteInt32LittleEndian(header, PayloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (byte)PayloadType);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), PayloadOption);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), TimestampNtp);
        return header;
    }

    /// <summary>
    /// A mirror packet's presentation stamp as nanoseconds on the SENDER's
    /// clock. The layout is NTP's fixed-point split — whole units above, a
    /// 2^-32 fraction below — but the value is NOT an NTP date: it is the
    /// sender's monotonic clock, counting from its own boot, with no
    /// 1900-epoch bias. Subtracting the epoch as if it were a date puts every
    /// frame exactly 2208988800 seconds in the past, which is a wrong answer
    /// that still looks like a plausible timestamp.
    /// </summary>
    public static long PresentationStampToNanos(ulong stamp)
    {
        var seconds = (long)(stamp >> 32);
        var fractionNanos = (long)(((ulong)(uint)stamp * 1_000_000_000UL) >> 32);
        return seconds * 1_000_000_000L + fractionNanos;
    }

    /// <summary>NTP-format timestamp (seconds since 1900 above, 2^-32 fraction below) as UNIX nanoseconds.</summary>
    public static long NtpToUnixNanos(ulong ntp)
    {
        const long ntpToUnixSeconds = 2_208_988_800L;
        var seconds = (long)(ntp >> 32) - ntpToUnixSeconds;
        var fractionNanos = (long)(((ulong)(uint)ntp * 1_000_000_000UL) >> 32);
        return seconds * 1_000_000_000L + fractionNanos;
    }

    /// <summary>Inverse of <see cref="NtpToUnixNanos"/>, for the stamps we emit.</summary>
    public static ulong UnixNanosToNtp(long unixNanos)
    {
        const long ntpToUnixSeconds = 2_208_988_800L;
        var seconds = unixNanos / 1_000_000_000L;
        var nanos = unixNanos % 1_000_000_000L;
        if (nanos < 0)
        {
            seconds--;
            nanos += 1_000_000_000L;
        }

        var fraction = (ulong)(((UInt128)nanos << 32) / 1_000_000_000);
        return ((ulong)(seconds + ntpToUnixSeconds) << 32) | (uint)fraction;
    }
}
