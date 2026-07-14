using Streamsemble.AirPlay.Common.Hap;

namespace Streamsemble.AirPlay.Common;

/// <summary>
/// Encrypts realtime AirPlay 2 audio payloads as OwnTone / airplay2-rs do:
/// ChaCha20-Poly1305 keyed by the 32-byte audio key (the <c>shk</c> sent in
/// SETUP). The nonce is the packet's <b>RTP sequence number</b> as two
/// little-endian bytes at offset 4 of a 12-byte block
/// (<c>[0,0,0,0, seq_lo, seq_hi, 0,0,0,0,0,0]</c>) — receivers derive the same
/// nonce from the RTP header, so it must match the sequence number, not a free
/// counter. AAD = RTP timestamp ‖ SSRC (header bytes [4..12)). The wire payload
/// after the 12-byte RTP header is <c>ciphertext ‖ 16-byte tag ‖ 8-byte nonce tail</c>.
/// </summary>
public sealed class AirPlay2AudioCipher(byte[] audioKey)
{
    public byte[] Encrypt(ushort sequenceNumber, ReadOnlySpan<byte> rtpHeader12, ReadOnlySpan<byte> payload)
    {
        var nonceTail = new byte[8];
        nonceTail[0] = (byte)sequenceNumber;        // seq low, little-endian
        nonceTail[1] = (byte)(sequenceNumber >> 8); // seq high
        return Encrypt(nonceTail, rtpHeader12, payload);
    }

    /// <summary>
    /// Buffered-stream variant: the receiver reads the nonce from the packet
    /// tail rather than deriving it, so any per-key-unique counter works.
    /// </summary>
    public byte[] Encrypt(byte[] nonceTail8, ReadOnlySpan<byte> rtpHeader12, ReadOnlySpan<byte> payload)
        => EncryptWithAad(nonceTail8, rtpHeader12.Slice(4, 8), payload); // timestamp (BE) ‖ SSRC (BE)

    /// <summary>Same envelope with caller-chosen AAD (legacy buffered receivers may authenticate different header bytes).</summary>
    public byte[] EncryptWithAad(byte[] nonceTail8, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> payload)
    {
        var nonce = PairingCrypto.Nonce(nonceTail8);
        var ciphertextWithTag = PairingCrypto.ChaCha20Poly1305Encrypt(audioKey, nonce, payload.ToArray(), aad.ToArray());

        var result = new byte[ciphertextWithTag.Length + 8];
        ciphertextWithTag.CopyTo(result, 0);
        nonceTail8.CopyTo(result, ciphertextWithTag.Length);
        return result;
    }
}
