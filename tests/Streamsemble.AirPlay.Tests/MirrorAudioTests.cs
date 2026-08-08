using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Streamsemble.AirPlay.Common;
using Streamsemble.AirPlay.Common.Hap;
using Streamsemble.AirPlay.Sender.Raop;
using Streamsemble.AirPlay.Sender.Video;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

/// <summary>
/// The outbound mirror's companion audio stream (type 96). The envelope must
/// be exactly the realtime AirPlay 2 one — [12-byte RTP][ciphertext][16-byte
/// tag][8-byte LE counter], AAD = header[4..12) — keyed by the raw 32-byte
/// shk from SETUP, with the nonce read from the trailer rather than derived
/// from the RTP sequence. Each test opens the packet the way a receiver
/// would, so a drift in any of those conventions fails here instead of as
/// silence on a TV.
/// </summary>
public class MirrorAudioTests
{
    private static byte[] SamplePcm()
    {
        var pcm = new byte[352 * 4];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(i * 31);
        }

        return pcm;
    }

    /// <summary>Opens a packet the receiver's way: nonce from the trailer, AAD from the header.</summary>
    private static byte[] Open(byte[] packet, byte[] key)
    {
        var nonce = new byte[12];
        packet.AsSpan(packet.Length - 8).CopyTo(nonce.AsSpan(4));
        return PairingCrypto.ChaCha20Poly1305Decrypt(key, nonce, packet[12..^8], packet.AsSpan(4, 8).ToArray());
    }

    [Fact]
    public void AudioPacketRoundTrips()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var pcm = SamplePcm();
        var packet = MirrorAudioPacket.Build(pcm, sequence: 0x1234, rtpTime: 0xAABBCCDD, nonceCounter: 5,
            new AirPlay2AudioCipher(key));

        Assert.Equal(0x80, packet[0]);
        Assert.Equal(0x60, packet[1]); // PT 96, no marker
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8))); // SSRC stays zero
        Assert.Equal(5ul, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(packet.Length - 8)));

        var expected = new byte[AlacPacker.PackedLength(352)];
        var length = AlacPacker.Pack(pcm, expected);
        Assert.Equal(expected.AsSpan(0, length).ToArray(), Open(packet, key));
    }

    [Fact]
    public void TamperedHeaderFailsAuthentication()
    {
        // AAD covers timestamp ‖ SSRC: rewriting the timestamp must void the tag.
        var key = RandomNumberGenerator.GetBytes(32);
        var packet = MirrorAudioPacket.Build(SamplePcm(), 1, 1000, 0, new AirPlay2AudioCipher(key));
        packet[5] ^= 0x01;
        Assert.Throws<InvalidCipherTextException>(() => Open(packet, key));
    }

    [Fact]
    public void NonceComesFromTheTrailerNotTheSequence()
    {
        // The speaker streams derive the nonce from the RTP sequence number;
        // mirror audio does NOT — it carries a free-running counter in the
        // trailer (the WinPlay convention, proven against tvOS). A packet
        // whose counter differs from its sequence must open only the trailer
        // way.
        var key = RandomNumberGenerator.GetBytes(32);
        var packet = MirrorAudioPacket.Build(SamplePcm(), sequence: 7, rtpTime: 1000, nonceCounter: 42,
            new AirPlay2AudioCipher(key));

        var sequenceNonce = new byte[12];
        sequenceNonce[4] = 7;
        Assert.Throws<InvalidCipherTextException>(() => PairingCrypto.ChaCha20Poly1305Decrypt(
            key, sequenceNonce, packet[12..^8], packet.AsSpan(4, 8).ToArray()));

        Assert.NotEmpty(Open(packet, key));
    }

    [Fact]
    public void SyncPacketIsAppleShaped()
    {
        // Apple's triple: [4..8) the sample audible NOW, [8..16) NTP now,
        // [16..20) the tx head. The receiver derives its buffer depth from
        // field3 − field1, so the pair must differ by exactly the send lead.
        // Half a second past a whole UNIX second → NTP fraction exactly 2^31.
        const long nowNanos = 1_754_000_000_500_000_000;
        const long audibleNanos = nowNanos + 1_500_000_000; // 1.5 s lead = 66150 samples
        var packet = MirrorAudioPacket.BuildSync(0xCAFEBABE, audibleNanos, nowNanos, first: false);

        Assert.Equal(20, packet.Length);
        Assert.Equal(0x80, packet[0]);
        Assert.Equal(0xD4, packet[1]);
        Assert.Equal(0x0007, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(0xCAFEBABEu - 66_150u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16)));

        var ntp = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(8));
        Assert.Equal(1_754_000_000UL + 2_208_988_800UL, ntp >> 32); // + the 1900→1970 epoch
        Assert.Equal(0x8000_0000UL, ntp & 0xFFFF_FFFF);
    }

    [Fact]
    public void SyncPacketNeverPutsTheAudibleSampleAheadOfTheHead()
    {
        // A late frame (audible instant already past) must clamp the lead to
        // zero, not wrap the subtraction into a huge positive one.
        var packet = MirrorAudioPacket.BuildSync(1000, audibleUnixNanos: 5, nowUnixNanos: 10, first: false);
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
    }

    [Fact]
    public void FirstSyncCarriesTheMarker()
    {
        Assert.Equal(0x90, MirrorAudioPacket.BuildSync(0, 0, 0, first: true)[0]);
        Assert.Equal(0x80, MirrorAudioPacket.BuildSync(0, 0, 0, first: false)[0]);
    }
}
