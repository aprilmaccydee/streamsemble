using System.Buffers.Binary;
using Streamsemble.AirPlay.Common;
using Streamsemble.AirPlay.Sender.Raop;

namespace Streamsemble.AirPlay.Sender.Video;

/// <summary>
/// Wire packets for a mirror session's companion audio stream (type 96) —
/// the stream that lets the mirrored display play sound without being a
/// member of the speaker group.
///
/// The envelope is the ordinary realtime AirPlay 2 one:
/// <c>[12-byte RTP header][ciphertext][16-byte tag][8-byte LE counter]</c>,
/// ChaCha20-Poly1305 keyed by the 32-byte <c>shk</c> we chose and sent in
/// SETUP, nonce = the trailer counter at offset 4 of 12, AAD = header bytes
/// [4..12) (timestamp ‖ SSRC). One convention differs from the speaker
/// streams: the nonce comes from a free-running counter carried in the
/// trailer, not from the RTP sequence number — mirror receivers read the
/// trailer (WinPlay MirrorSession, proven against tvOS).
/// </summary>
public static class MirrorAudioPacket
{
    public const int RtpHeaderLength = 12;

    /// <summary>Builds one audio packet: S16LE stereo PCM → ALAC verbatim → sealed envelope.</summary>
    public static byte[] Build(ReadOnlySpan<byte> pcmS16Le, ushort sequence, uint rtpTime, ulong nonceCounter, AirPlay2AudioCipher cipher)
    {
        var header = new byte[RtpHeaderLength];
        header[0] = 0x80;
        header[1] = 0x60; // PT 96, never a marker — Apple's mirror audio doesn't set one
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), rtpTime);
        // SSRC stays zero.

        var alac = new byte[AlacPacker.PackedLength(pcmS16Le.Length / 4)];
        var alacLength = AlacPacker.Pack(pcmS16Le, alac);

        var nonceTail = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(nonceTail, nonceCounter);
        var sealedPayload = cipher.Encrypt(nonceTail, header, alac.AsSpan(0, alacLength));

        var packet = new byte[RtpHeaderLength + sealedPayload.Length];
        header.CopyTo(packet, 0);
        sealedPayload.CopyTo(packet.AsSpan(RtpHeaderLength));
        return packet;
    }

    /// <summary>
    /// The 0xD4 sync packet, Apple's shape: [4..8) the sample turning audible
    /// NOW, [8..16) NTP now (on the clock the display disciplines to through
    /// <see cref="MirrorNtpServer"/>), [16..20) the sample being TRANSMITTED
    /// now. The receiver reads the absolute mapping from the first pair and
    /// its buffer depth from the third minus the first. Writing the tx head in
    /// both rtp fields (as the first cut did) states the same mapping but a
    /// zero depth — and the display then plays on arrival, a whole send-lead
    /// early. The lead is derived, never estimated: it is exactly how far the
    /// frame's audible instant sits past now, i.e. the group's own pacing.
    /// </summary>
    public static byte[] BuildSync(uint rtpNow, long audibleUnixNanos, long nowUnixNanos, bool first)
    {
        var leadSamples = (uint)(Math.Max(0, audibleUnixNanos - nowUnixNanos) * 44100 / 1_000_000_000);
        var packet = new byte[20];
        packet[0] = (byte)(first ? 0x90 : 0x80);
        packet[1] = 0xD4; // 0x54 | marker
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x0007);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), rtpNow - leadSamples);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), MirrorNtpServer.Ntp(nowUnixNanos));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), rtpNow);
        return packet;
    }
}
