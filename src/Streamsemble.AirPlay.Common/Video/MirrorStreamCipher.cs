using System.Security.Cryptography;
using System.Text;
using Streamsemble.AirPlay.Common.Hap;

namespace Streamsemble.AirPlay.Common.Video;

/// <summary>
/// The envelope protecting a mirroring data channel's video payloads.
/// </summary>
public interface IMirrorStreamCipher
{
    /// <summary>Bytes the envelope adds to a payload (0 for a stream cipher, 16 for an AEAD tag).</summary>
    int Overhead { get; }

    /// <summary>How the key was derived, for logs.</summary>
    string Describe();

    byte[] Encrypt(ReadOnlySpan<byte> plaintext);

    byte[] Decrypt(ReadOnlySpan<byte> ciphertext);
}

/// <summary>
/// AES-128 in counter mode — the envelope every documented AirPlay mirroring
/// implementation uses for type-0 video payloads. Two things about it are easy
/// to get wrong and both are load-bearing:
/// <list type="bullet">
/// <item>The counter is CONTINUOUS across the whole stream, not reset per
///   packet, so this object is stateful and belongs to exactly one direction of
///   one data channel.</item>
/// <item>Each packet nonetheless starts on a fresh counter block: leftover
///   keystream from a previous packet's partial trailing block is discarded,
///   never carried over. Carrying it forward decrypts packet 1 correctly and
///   turns everything after it into noise.</item>
/// </list>
/// The key and IV come from SHA-512 over a label, the stream connection ID in
/// decimal, and the 16-byte stream key: <c>SHA-512("AirPlayStreamKey" ‖ id ‖
/// key)[..16]</c> and the same with <c>"AirPlayStreamIV"</c>. Where that 16-byte
/// stream key itself comes from is the caller's problem — see
/// <see cref="MirrorKeySource"/>.
/// </summary>
public sealed class MirrorAesCtrCipher : IMirrorStreamCipher, IDisposable
{
    private const string KeyLabel = "AirPlayStreamKey";
    private const string IvLabel = "AirPlayStreamIV";
    private const int BlockSize = 16;

    private readonly Aes _aes;
    private readonly ICryptoTransform _blockCipher;
    private readonly byte[] _counter;
    private readonly MirrorKeySource _source;

    public MirrorAesCtrCipher(ReadOnlySpan<byte> streamKey, ulong streamConnectionId, MirrorKeySource source)
    {
        if (streamKey.Length < BlockSize)
        {
            throw new ArgumentException($"mirror stream key must be at least {BlockSize} bytes", nameof(streamKey));
        }

        _source = source;
        var key = Derive(KeyLabel, streamConnectionId, streamKey);
        _counter = Derive(IvLabel, streamConnectionId, streamKey);

        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = key;
        // Counter mode is built from raw block encryptions in both directions,
        // so one encryptor transform serves encrypt and decrypt alike.
        _blockCipher = _aes.CreateEncryptor();
    }

    /// <summary>
    /// The stream key material both ends derive before either the key or the IV
    /// exists: <c>SHA-512(aesKey ‖ ecdhSecret)</c>, truncated to 16 bytes.
    ///
    /// This stage is easy to miss because the second one looks self-contained,
    /// and missing it produces a cipher that runs perfectly and decrypts
    /// everything to noise. <paramref name="aesKey"/> is the FairPlay-unwrapped
    /// <c>ekey</c> when a sender sent one and empty when it did not — a
    /// transiently-paired screen mirror sends none, so its material is the
    /// pairing secret alone.
    /// </summary>
    public static byte[] DeriveKeyMaterial(ReadOnlySpan<byte> aesKey, ReadOnlySpan<byte> ecdhSecret)
    {
        var input = new byte[aesKey.Length + ecdhSecret.Length];
        aesKey.CopyTo(input);
        ecdhSecret.CopyTo(input.AsSpan(aesKey.Length));
        return SHA512.HashData(input)[..BlockSize];
    }

    /// <summary>
    /// SHA-512(label ‖ decimal stream connection id ‖ key material), truncated
    /// to an AES-128 block. The id is spelled UNSIGNED: macOS sends it as a
    /// negative signed integer, and rendering it that way decrypts correctly
    /// only until the value happens to go negative.
    /// </summary>
    internal static byte[] Derive(string label, ulong streamConnectionId, ReadOnlySpan<byte> streamKey)
    {
        var prefix = Encoding.ASCII.GetBytes(label + streamConnectionId.ToString());
        var input = new byte[prefix.Length + streamKey.Length];
        prefix.CopyTo(input, 0);
        streamKey.CopyTo(input.AsSpan(prefix.Length));
        return SHA512.HashData(input)[..BlockSize];
    }

    public int Overhead => 0;

    public string Describe() => $"AES-128-CTR ({_source.Describe()})";

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) => Transform(plaintext);

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) => Transform(ciphertext);

    private byte[] Transform(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        var keystream = new byte[BlockSize];

        for (var offset = 0; offset < data.Length; offset += BlockSize)
        {
            _blockCipher.TransformBlock(_counter, 0, BlockSize, keystream, 0);
            IncrementCounter();

            var count = Math.Min(BlockSize, data.Length - offset);
            for (var i = 0; i < count; i++)
            {
                output[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
            }
        }

        return output;
    }

    /// <summary>Big-endian increment over the whole 16-byte block, as every CTR implementation here does it.</summary>
    private void IncrementCounter()
    {
        for (var i = _counter.Length - 1; i >= 0; i--)
        {
            if (++_counter[i] != 0)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _blockCipher.Dispose();
        _aes.Dispose();
    }
}

/// <summary>
/// ChaCha20-Poly1305 per packet, keyed straight from a HAP pairing's shared
/// secret — the same envelope and the same key the AirPlay 2 audio streams
/// already use, with a little-endian packet counter as the nonce and the
/// packet header authenticated as additional data.
/// </summary>
/// <remarks>
/// This exists because a HAP-paired mirror session has no FairPlay key to
/// derive from and reusing the session's audio envelope is the obvious
/// candidate — but it is a hypothesis, not an observation: no capture in
/// <c>debug/airplay-mirror/</c> has yet shown a sender using it. Selecting this
/// mode logs that it is unverified. <see cref="MirrorAesCtrCipher"/> is the
/// mode every documented implementation actually uses.
/// </remarks>
public sealed class MirrorChaChaCipher(byte[] sharedSecret) : IMirrorStreamCipher
{
    private readonly byte[] _key = sharedSecret.Length >= 32
        ? sharedSecret[..32]
        : throw new ArgumentException("mirror ChaCha key needs at least 32 bytes of shared secret", nameof(sharedSecret));

    private ulong _sendCounter;
    private ulong _receiveCounter;

    public int Overhead => 16;

    public string Describe() => "ChaCha20-Poly1305 (HAP shared secret, UNVERIFIED against a capture)";

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) =>
        PairingCrypto.ChaCha20Poly1305Encrypt(_key, PairingCrypto.CounterNonce(_sendCounter++), plaintext.ToArray());

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) =>
        PairingCrypto.ChaCha20Poly1305Decrypt(_key, PairingCrypto.CounterNonce(_receiveCounter++), ciphertext.ToArray());
}

/// <summary>Where the 16 bytes feeding the mirror key derivation came from.</summary>
public enum MirrorKeySource
{
    /// <summary>
    /// <c>shk</c> from the stream SETUP of a HAP-paired session: the sender
    /// hands us the key directly because the pairing already authenticated it.
    /// </summary>
    SharedKey,

    /// <summary>
    /// <c>ekey</c> from the stream SETUP, unwrapped through FairPlay against
    /// the <c>fp-setup</c> handshake. What a sender that declines HAP uses.
    /// </summary>
    FairPlay,

    /// <summary>
    /// The HAP pairing secret, when the SETUP names no key at all. What macOS
    /// screen mirroring does: it sets
    /// <c>streamConnectionKeyUseStreamEncryptionKey</c> and expects the key to
    /// come from the pairing both ends already completed.
    /// </summary>
    PairingSecret,
}

public static class MirrorKeySourceExtensions
{
    public static string Describe(this MirrorKeySource source) => source switch
    {
        MirrorKeySource.SharedKey => "shk from a HAP-paired SETUP",
        MirrorKeySource.FairPlay => "ekey unwrapped through FairPlay",
        MirrorKeySource.PairingSecret => "the HAP pairing secret (SETUP named no key)",
        _ => source.ToString(),
    };
}

/// <summary>
/// Pass-through, for a mirror stream that turns out not to be encrypted at all.
/// Worth having as a real cipher rather than a special case: the rest of the
/// path then needs no knowledge of whether a key was ever involved.
/// </summary>
public sealed class NullMirrorCipher : IMirrorStreamCipher
{
    public int Overhead => 0;

    public string Describe() => "unencrypted";

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray();
}
