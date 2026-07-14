using Streamsemble.AirPlay.Common;
using Streamsemble.AirPlay.Common.Hap;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class AirPlay2CipherTests
{
    [Fact]
    public void AudioKeyIsFirst32BytesOfSharedSecret()
    {
        var secret = new byte[64];
        for (var i = 0; i < 64; i++)
        {
            secret[i] = (byte)i;
        }

        var keys = HapSessionKeys.Derive(secret, controllerRole: true);
        Assert.Equal(secret[..32], keys.AudioKey);
        // Control keys are HKDF-derived, so must differ from the raw secret slices.
        Assert.NotEqual(keys.AudioKey, keys.ControlWriteKey);
        Assert.NotEqual(keys.ControlReadKey, keys.ControlWriteKey);
    }

    [Fact]
    public void ControllerAndAccessoryControlKeysAreMirrored()
    {
        var secret = new byte[64];
        Array.Fill(secret, (byte)0x5A);
        var controller = HapSessionKeys.Derive(secret, controllerRole: true);
        var accessory = HapSessionKeys.Derive(secret, controllerRole: false);

        // What the controller writes, the accessory reads, and vice versa.
        Assert.Equal(controller.ControlWriteKey, accessory.ControlReadKey);
        Assert.Equal(controller.ControlReadKey, accessory.ControlWriteKey);
    }

    [Fact]
    public void AudioPacketLayoutIsCiphertextTagNonce()
    {
        var audioKey = new byte[32];
        var cipher = new AirPlay2AudioCipher(audioKey);
        var header = new byte[12];
        header[0] = 0x80;
        header[1] = 0x60;
        var payload = new byte[352 * 4];

        var wire = cipher.Encrypt(0x1234, header, payload);

        // ciphertext (== payload length) + 16-byte tag + 8-byte nonce tail.
        Assert.Equal(payload.Length + 16 + 8, wire.Length);
        // Nonce tail carries the RTP sequence number little-endian in its first two bytes.
        Assert.Equal(0x34, wire[^8]);
        Assert.Equal(0x12, wire[^7]);
        Assert.Equal(new byte[6], wire[^6..]);
    }

    [Fact]
    public void AudioPacketDecryptsWithSeqNonceAndHeaderAad()
    {
        var audioKey = new byte[32];
        Array.Fill(audioKey, (byte)0x11);
        var cipher = new AirPlay2AudioCipher(audioKey);
        var header = new byte[12];
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(header, 4); // timestamp portion of AAD
        var payload = "airplay realtime frame"u8.ToArray();

        var wire = cipher.Encrypt(0x0042, header, payload);
        var ciphertextWithTag = wire[..^8];
        var nonceTail = wire[^8..];

        // Nonce is reconstructible from the RTP sequence number.
        var expectedNonce = new byte[8];
        expectedNonce[0] = 0x42;
        Assert.Equal(expectedNonce, nonceTail);

        var nonce = PairingCrypto.Nonce(nonceTail);
        var aad = header.AsSpan(4, 8).ToArray();
        var decrypted = PairingCrypto.ChaCha20Poly1305Decrypt(audioKey, nonce, ciphertextWithTag, aad);
        Assert.Equal(payload, decrypted);
    }
}
