using System.Buffers.Binary;
using System.Security.Cryptography;
using Streamsemble.AirPlay.Common.Hap;

namespace Streamsemble.AirPlay.Common.FairPlay;

/// <summary>
/// Unwraps the stream key a sender hides in a SETUP's <c>ekey</c>.
///
/// The record a sender emits is
/// <c>[0:16]</c> header, <c>[16:32]</c> a per-key random mask, <c>[32:36]</c> the
/// raw key's length, <c>[36:56]</c> an HMAC, and <c>[56:72]</c> the raw key XORed
/// with the mask and then AES-encrypted. So recovering the key is a decrypt and
/// an XOR — all the difficulty is in the wrapping key, which neither side
/// transmits. Both sides derive it from the same two things: the SAP value the
/// receiver sent in m2, and the whole m3 message the sender replied with.
///
/// That is what makes this work for us without any captured material. We are
/// the receiver, so our m2 is our own canned reply and its SAP is ours to
/// decrypt; m3 is the <c>fp-setup</c> phase-2 body the session retained. The
/// white-box network tables that dominate published FairPlay implementations
/// are not needed here at all — those exist to produce m3's 20-byte tail, which
/// is a sender's obligation, and the receiver only echoes it back in m4.
/// </summary>
/// <remarks>
/// Behaviour is pinned by known-answer vectors in the test project, including
/// one captured <c>m3</c>/<c>ekey</c> pair whose expected output comes from an
/// independent reference implementation. If those pass, this is correct; there
/// is no partial credit with a key unwrap.
/// </remarks>
public sealed class FairPlayKeyDecryptor : IFairPlayKeyDecryptor
{
    /// <summary>Offset of the per-key random mask inside the 72-byte record.</summary>
    private const int MaskOffset = 16;

    /// <summary>Offset of the big-endian raw-key length.</summary>
    private const int KeyLengthOffset = 32;

    /// <summary>Offset of the wrapped key block.</summary>
    private const int WrappedKeyOffset = 56;

    private const int EkeyLength = 72;
    private const int KeyLength = 16;

    public byte[] UnwrapStreamKey(ReadOnlySpan<byte> keyMessage, ReadOnlySpan<byte> ekey)
    {
        if (ekey.Length != EkeyLength)
        {
            throw new ArgumentException($"FairPlay ekey must be {EkeyLength} bytes, got {ekey.Length}", nameof(ekey));
        }

        var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(ekey[KeyLengthOffset..]);
        if (declaredLength != KeyLength)
        {
            throw new InvalidDataException($"FairPlay ekey declares a {declaredLength}-byte key, expected {KeyLength}");
        }

        var mode = keyMessage[FairPlayMessageCipher.ModeOffset];
        return Unwrap(ReceiverSapForMode(mode), keyMessage, ekey);
    }

    /// <summary>
    /// The plaintext SAP behind the phase-1 reply we send for this mode. The
    /// sender wrapped its key against exactly these bytes.
    /// </summary>
    internal static byte[] ReceiverSapForMode(byte mode)
    {
        // DecryptMessage reads the mode from byte 12 and the body from byte 16,
        // which is m3's layout; the phase-1 reply packs the same body at a
        // different offset, so restate it in the shape the cipher expects.
        var shaped = new byte[FairPlayMessageCipher.BodyOffset + FairPlayMessageCipher.SapLength];
        shaped[FairPlayMessageCipher.ModeOffset] = mode;
        FairPlaySetup.ReplySapBody(mode).CopyTo(shaped.AsSpan(FairPlayMessageCipher.BodyOffset));

        var sap = new byte[FairPlayMessageCipher.SapLength];
        FairPlayMessageCipher.DecryptMessage(shaped, sap);
        return sap;
    }

    /// <summary>The unwrap itself, with the receiver SAP supplied — the form the known-answer vectors exercise.</summary>
    internal static byte[] Unwrap(ReadOnlySpan<byte> receiverSap, ReadOnlySpan<byte> m3, ReadOnlySpan<byte> ekey)
    {
        var wrappingKey = DeriveWrappingKey(receiverSap, m3);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = wrappingKey;
        using var decryptor = aes.CreateDecryptor();

        var key = new byte[KeyLength];
        decryptor.TransformBlock(ekey.Slice(WrappedKeyOffset, KeyLength).ToArray(), 0, KeyLength, key, 0);
        for (var i = 0; i < key.Length; i++)
        {
            key[i] ^= ekey[MaskOffset + i];
        }

        return key;
    }

    /// <summary>
    /// Derives the 16-byte AES key that wraps the stream key, from the
    /// receiver's SAP and the sender's m3.
    ///
    /// The material is a fixed 290-byte record — a constant prefix, m3's
    /// decrypted SAP, the receiver's SAP, a constant suffix — followed by
    /// ordinary MD5 padding out to 320 bytes. The compression is not MD5
    /// though: each 64-byte block is run through BOTH the modified MD5 and the
    /// SAP hash, and the two results are added word-wise.
    /// </summary>
    internal static byte[] DeriveWrappingKey(ReadOnlySpan<byte> receiverSap, ReadOnlySpan<byte> m3)
    {
        var senderSap = new byte[FairPlayMessageCipher.SapLength];
        FairPlayMessageCipher.DecryptMessage(m3, senderSap);

        var material = new byte[320];
        var offset = 0;
        FairPlayTables.KdfPrefix.CopyTo(material, offset);
        offset += FairPlayTables.KdfPrefix.Length;
        senderSap.CopyTo(material, offset);
        offset += senderSap.Length;
        receiverSap[..FairPlayMessageCipher.SapLength].CopyTo(material.AsSpan(offset));
        offset += FairPlayMessageCipher.SapLength;
        FairPlayTables.KdfSuffix.CopyTo(material, offset);
        offset += FairPlayTables.KdfSuffix.Length;

        material[offset] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(material.AsSpan(material.Length - 8), (ulong)offset * 8);

        var state = FairPlayMd5.WordsFromLittleEndian(FairPlayTables.InitialSessionKey);
        for (var block = 0; block < material.Length; block += FairPlayMd5.BlockSize)
        {
            var chunk = material.AsSpan(block, FairPlayMd5.BlockSize);
            var compressed = FairPlayMd5.Compress(state, chunk, FairPlayMd5Mutation.Kdf);
            var hashed = FairPlaySapHash.Compute(chunk);
            for (var word = 0; word < state.Length; word++)
            {
                state[word] = unchecked(compressed[word] + BinaryPrimitives.ReadUInt32LittleEndian(hashed.AsSpan(word * 4)));
            }
        }

        return FairPlayMd5.WordsToBigEndian(state);
    }
}
