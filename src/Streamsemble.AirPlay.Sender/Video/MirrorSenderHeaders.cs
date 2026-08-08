using System.Buffers.Binary;
using Streamsemble.AirPlay.Common.Video;

namespace Streamsemble.AirPlay.Sender.Video;

/// <summary>
/// The 128-byte headers an outbound mirror puts in front of its packets,
/// byte-for-byte what real senders emit (WinPlay MirrorVideoStream / doubletake
/// mirror.go). The receiver side parses only the first 16 bytes and ignores the
/// rest, which hid how load-bearing the rest is when SENDING to a real TV:
/// <list type="bullet">
/// <item>The keyframe flag lives at byte [5] (0x10), not in the option word at
///   [6..8).</item>
/// <item>The codec-config header says WHAT it carries ([6]=0x16 [7]=0x01, the
///   "H.264 SPS+PPS" option) and carries the encode and display dimensions as
///   floats — a TV configures its render pipeline from these, and a header of
///   zeros gives it nothing to configure with.</item>
/// <item>Heartbeats carry [6]=0x1e and a live timestamp.</item>
/// </list>
/// </summary>
public static class MirrorSenderHeaders
{
    public static byte[] Video(int payloadLength, bool keyframe, ulong ntpStamp)
    {
        var header = new byte[MirrorPacketHeader.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payloadLength);
        header[4] = (byte)MirrorPayloadType.Video;
        header[5] = keyframe ? (byte)0x10 : (byte)0x00;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), ntpStamp);
        return header;
    }

    public static byte[] CodecConfig(int payloadLength, ulong ntpStamp,
        float sourceWidth, float sourceHeight, float displayWidth, float displayHeight)
    {
        var header = new byte[MirrorPacketHeader.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payloadLength);
        header[4] = (byte)MirrorPayloadType.CodecConfig;
        header[6] = 0x16; // H.264 SPS+PPS option
        header[7] = 0x01;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), ntpStamp);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(16), sourceWidth);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(20), sourceHeight);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(40), sourceWidth);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(44), sourceHeight);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(56), displayWidth);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(60), displayHeight);
        return header;
    }

    public static byte[] Heartbeat(ulong ntpStamp)
    {
        var header = new byte[MirrorPacketHeader.Length];
        header[4] = (byte)MirrorPayloadType.Heartbeat;
        header[6] = 0x1e;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), ntpStamp);
        return header;
    }
}
