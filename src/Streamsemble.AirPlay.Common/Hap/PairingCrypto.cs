using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// The cryptographic primitives HAP pairing needs, wrapped over BouncyCastle
/// so the pairing state machines read as protocol rather than crypto:
/// HKDF-SHA512, X25519, Ed25519, and ChaCha20-Poly1305 with HAP's nonce rule.
/// </summary>
public static class PairingCrypto
{
    /// <summary>HKDF-SHA512 (extract+expand).</summary>
    public static byte[] HkdfSha512(byte[] ikm, byte[] salt, byte[] info, int length)
    {
        var hkdf = new Org.BouncyCastle.Crypto.Generators.HkdfBytesGenerator(new Sha512Digest());
        hkdf.Init(new HkdfParameters(ikm, salt, info));
        var output = new byte[length];
        hkdf.GenerateBytes(output, 0, length);
        return output;
    }

    public static byte[] HkdfSha512(byte[] ikm, string salt, string info, int length)
        => HkdfSha512(ikm, System.Text.Encoding.ASCII.GetBytes(salt), System.Text.Encoding.ASCII.GetBytes(info), length);

    // --- X25519 -------------------------------------------------------------

    public static (byte[] PrivateKey, byte[] PublicKey) GenerateX25519()
    {
        var generator = new X25519KeyPairGenerator();
        generator.Init(new X25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var priv = ((X25519PrivateKeyParameters)pair.Private).GetEncoded();
        var pub = ((X25519PublicKeyParameters)pair.Public).GetEncoded();
        return (priv, pub);
    }

    public static byte[] X25519SharedSecret(byte[] ourPrivate, byte[] theirPublic)
    {
        var priv = new X25519PrivateKeyParameters(ourPrivate);
        var pub = new X25519PublicKeyParameters(theirPublic);
        var agreement = new Org.BouncyCastle.Crypto.Agreement.X25519Agreement();
        agreement.Init(priv);
        var secret = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(pub, secret, 0);
        return secret;
    }

    // --- Ed25519 ------------------------------------------------------------

    public static (byte[] PrivateSeed, byte[] PublicKey) GenerateEd25519()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var priv = ((Ed25519PrivateKeyParameters)pair.Private).GetEncoded();
        var pub = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
        return (priv, pub);
    }

    public static byte[] Ed25519PublicFromSeed(byte[] seed)
        => new Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded();

    public static byte[] Ed25519Sign(byte[] seed, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    public static bool Ed25519Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }

    // --- ChaCha20-Poly1305 --------------------------------------------------

    /// <summary>
    /// HAP nonce: 12 bytes, the 8-byte counter/string right-justified into the
    /// low bytes with 4 leading zero bytes. A short ASCII nonce like "PS-Msg05"
    /// is used verbatim as the 8-byte tail.
    /// </summary>
    public static byte[] Nonce(byte[] eightBytes)
    {
        if (eightBytes.Length is not (8 or 12))
        {
            var padded = new byte[12];
            eightBytes.CopyTo(padded, 12 - eightBytes.Length);
            return padded;
        }

        if (eightBytes.Length == 12)
        {
            return eightBytes;
        }

        var nonce = new byte[12];
        eightBytes.CopyTo(nonce, 4);
        return nonce;
    }

    public static byte[] Nonce(string ascii) => Nonce(System.Text.Encoding.ASCII.GetBytes(ascii));

    /// <summary>64-bit little-endian counter as a HAP nonce (used for the encrypted control/RTSP channel).</summary>
    public static byte[] CounterNonce(ulong counter)
    {
        var nonce = new byte[12];
        BitConverter.TryWriteBytes(nonce.AsSpan(4), counter);
        if (!BitConverter.IsLittleEndian)
        {
            nonce.AsSpan(4, 8).Reverse();
        }

        return nonce;
    }

    public static byte[] ChaCha20Poly1305Encrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[]? aad = null)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce, aad));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        cipher.DoFinal(output, len);
        return output; // ciphertext || 16-byte tag
    }

    public static byte[] ChaCha20Poly1305Decrypt(byte[] key, byte[] nonce, byte[] ciphertextWithTag, byte[]? aad = null)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, nonce, aad));
        var output = new byte[cipher.GetOutputSize(ciphertextWithTag.Length)];
        var len = cipher.ProcessBytes(ciphertextWithTag, 0, ciphertextWithTag.Length, output, 0);
        cipher.DoFinal(output, len);
        return output;
    }
}
