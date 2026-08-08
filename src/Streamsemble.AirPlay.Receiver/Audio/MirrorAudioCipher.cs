using System.Security.Cryptography;

namespace Streamsemble.AirPlay.Receiver.Audio;

/// <summary>
/// The envelope on a screen mirror's companion audio: AES-128-CBC, re-keyed
/// from the same material as the video and re-initialised from the same IV on
/// every packet.
///
/// It is worth being explicit about how this differs from the realtime audio a
/// music session sends, because the two arrive on the same stream type and look
/// identical on the wire until you try to open one:
/// <list type="bullet">
/// <item>Key: <c>SHA-512(aesKey ‖ ecdhSecret)[:16]</c> — the very same
///   <c>eaeskey</c> the video stream derives its key and IV from, rather than a
///   <c>shk</c> the sender chose and transmitted.</item>
/// <item>IV: the <c>eiv</c> from the session SETUP, reused for every packet
///   rather than a per-packet counter.</item>
/// <item>No authentication tag. A wrong key here produces noise silently, with
///   none of the certainty a Poly1305 tag gives — which is exactly why hunting
///   this by trying keys and waiting for one to authenticate never terminated.</item>
/// <item>Only whole 16-byte blocks are encrypted. The trailing bytes of a
///   packet (fewer than a block) are sent in the CLEAR and appended verbatim —
///   decrypting them turns the tail of every frame to noise.</item>
/// </list>
/// </summary>
public sealed class MirrorAudioCipher(byte[] key, byte[] iv) : IDisposable
{
    private const int BlockSize = 16;

    private readonly Aes _aes = CreateAes(key, iv);

    private static Aes CreateAes(byte[] key, byte[] iv)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key.Length >= BlockSize
            ? key[..BlockSize]
            : throw new ArgumentException($"mirror audio key must be at least {BlockSize} bytes", nameof(key));
        aes.IV = iv.Length >= BlockSize ? iv[..BlockSize] : new byte[BlockSize];
        return aes;
    }

    /// <summary>
    /// Decrypts one RTP payload. The chain restarts from the SETUP IV each
    /// time — packets are independent, which is what lets a lossy realtime
    /// stream survive a dropped one.
    /// </summary>
    public byte[] Decrypt(ReadOnlySpan<byte> payload)
    {
        var whole = payload.Length & ~(BlockSize - 1);
        var output = new byte[payload.Length];

        if (whole > 0)
        {
            using var decryptor = _aes.CreateDecryptor();
            decryptor.TransformBlock(payload[..whole].ToArray(), 0, whole, output, 0);
        }

        // The sub-block tail rides in the clear.
        payload[whole..].CopyTo(output.AsSpan(whole));
        return output;
    }

    public void Dispose() => _aes.Dispose();
}
