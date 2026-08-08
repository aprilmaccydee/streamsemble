using System.Buffers.Binary;
using System.Text;
using Streamsemble.AirPlay.Common.Hap;

namespace Streamsemble.AirPlay.Common.Video;

/// <summary>
/// The modern AirPlay 2 mirroring envelope: ChaCha20-Poly1305 over each video
/// payload, keyed by HKDF from the pairing secret.
///
/// This is the path a HomeKit-paired sender uses, and it is a different scheme
/// from <see cref="MirrorAesCtrCipher"/> end to end — not a different key for
/// the same cipher. That distinction cost a lot of rounds: AES-CTR produces
/// output for any key, so pointing it at this stream yields plausible-looking
/// noise and no indication that the cipher itself is wrong.
///
/// The pieces, none of which are negotiated on the wire:
/// <list type="bullet">
/// <item>Key: HKDF-SHA512 over the pairing's shared secret, salted with
///   <c>"DataStream-Salt"</c> followed by the stream connection ID as an
///   UNSIGNED decimal, info <c>"DataStream-Output-Encryption-Key"</c>.</item>
/// <item>Nonce: 12 bytes, a little-endian packet counter at offset 4 starting
///   at zero. It is never transmitted — both ends just count, which is safe
///   here because the data channel is TCP and cannot reorder.</item>
/// <item>The 128-byte packet header is authenticated as additional data, so a
///   tampered header fails the tag rather than mis-framing the stream.</item>
/// <item>The tag is appended to the ciphertext; there is no nonce tail, unlike
///   the realtime AUDIO envelope this otherwise resembles.</item>
/// </list>
/// </summary>
public sealed class MirrorDataStreamCipher : IMirrorStreamCipher
{
    /// <summary>Poly1305 tag length appended to every payload.</summary>
    private const int TagLength = 16;

    private readonly byte[] _key;
    private readonly string _direction;
    private ulong _counter;

    private MirrorDataStreamCipher(byte[] key, string direction)
    {
        _key = key;
        _direction = direction;
    }

    /// <summary>
    /// The two HKDF directions this stream might be keyed with. Which one a
    /// sender encrypts with is a naming convention we cannot read off the wire,
    /// but the Poly1305 tag settles it on the first packet with certainty —
    /// unlike a stream cipher, an AEAD says "no" out loud.
    /// </summary>
    public static IReadOnlyList<MirrorDataStreamCipher> Candidates(byte[] sharedSecret, ulong streamConnectionId) =>
    [
        Create(sharedSecret, streamConnectionId, "DataStream-Output-Encryption-Key"),
        Create(sharedSecret, streamConnectionId, "DataStream-Input-Encryption-Key"),
    ];

    public static MirrorDataStreamCipher Create(byte[] sharedSecret, ulong streamConnectionId, string info) =>
        new(
            PairingCrypto.HkdfSha512(
                sharedSecret,
                Encoding.ASCII.GetBytes($"DataStream-Salt{streamConnectionId}"),
                Encoding.ASCII.GetBytes(info),
                32),
            info);

    public int Overhead => TagLength;

    public string Describe() => $"ChaCha20-Poly1305 (HKDF {_direction})";

    /// <summary>Packets consumed so far — the nonce counter, exposed so a trial can be replayed from zero.</summary>
    public ulong PacketCount => _counter;

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) =>
        throw new NotSupportedException("mirror data-stream encryption needs the packet header as AAD");

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) =>
        throw new NotSupportedException("mirror data-stream decryption needs the packet header as AAD");

    /// <summary>
    /// Seals one payload against its header: ciphertext ‖ 16-byte tag, the
    /// exact envelope <see cref="Open"/> undoes. The header goes out exactly
    /// as passed here — it is the AAD, so re-serializing it after sealing
    /// would fail the receiver's tag check. Note the header's length field
    /// must already count the tag (plaintext + 16).
    /// </summary>
    public byte[] Seal(ReadOnlySpan<byte> header, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _counter);

        var sealedPayload = PairingCrypto.ChaCha20Poly1305Encrypt(
            _key, nonce.ToArray(), plaintext.ToArray(), header.ToArray());
        _counter++;
        return sealedPayload;
    }

    /// <summary>
    /// Decrypts one payload against its header. Throws when the tag does not
    /// verify, which is the signal that this key or direction is wrong — the
    /// caller can try the other one because nothing has been consumed on
    /// failure except a counter it can reset.
    /// </summary>
    public byte[] Open(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < TagLength)
        {
            throw new InvalidDataException($"mirror video payload of {payload.Length} B is shorter than its tag");
        }

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _counter);

        var plaintext = PairingCrypto.ChaCha20Poly1305Decrypt(
            _key, nonce.ToArray(), payload.ToArray(), header.ToArray());
        _counter++;
        return plaintext;
    }

    /// <summary>Rewinds the packet counter, so a failed trial leaves no trace.</summary>
    public void Reset() => _counter = 0;
}
