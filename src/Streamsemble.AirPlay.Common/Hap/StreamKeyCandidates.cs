using System.Security.Cryptography;
using System.Text;

namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>One possible stream key, with a name so the log can say which one won.</summary>
public sealed record StreamKeyCandidate(string Name, byte[] Key);

/// <summary>
/// The keys a stream might be encrypted with when the SETUP names none.
///
/// A screen-mirroring session sets
/// <c>streamConnectionKeyUseStreamEncryptionKey</c> and sends no <c>shk</c> and
/// no <c>ekey</c>, so the key has to come from the pairing — but "from the
/// pairing" covers several derivations, and the protocol never says which. What
/// makes this tractable rather than a guessing game is that both stream types
/// carry their own proof of correctness: the audio payload is
/// ChaCha20-Poly1305, whose tag rejects a wrong key with probability
/// 1 − 2⁻¹²⁸, and the video payload is AVCC, whose length prefixes have to tile
/// the packet exactly. So the receiver tries the candidates against real
/// traffic and keeps the one that verifies.
///
/// Ordered most to least likely, so the usual case is decided on the first try.
/// </summary>
public static class StreamKeyCandidates
{
    /// <summary>
    /// Derivations from a pairing's shared secret. The 32-byte forms feed
    /// ChaCha directly; the mirror cipher takes the first 16 of whichever wins.
    /// </summary>
    public static IReadOnlyList<StreamKeyCandidate> FromSharedSecret(byte[] sharedSecret)
    {
        if (sharedSecret.Length < 32)
        {
            return [];
        }

        return
        [
            // What this project's own sender puts in shk for an ordinary
            // session, so the first thing to try for an extraordinary one.
            new("shared secret", sharedSecret[..32]),

            // A hash of the secret rather than the secret itself — the usual
            // way a protocol avoids reusing key material across purposes.
            new("SHA-512(shared secret)", SHA512.HashData(sharedSecret)[..32]),

            // The HKDF outputs the pairing already defines. The control-channel
            // keys are the obvious candidates because they are the ones both
            // ends compute unconditionally.
            new("HKDF Control-Write", Hkdf(sharedSecret, HapConstants.ControlSalt, HapConstants.ControlWriteInfo)),
            new("HKDF Control-Read", Hkdf(sharedSecret, HapConstants.ControlSalt, HapConstants.ControlReadInfo)),
            new("HKDF Events-Write", Hkdf(sharedSecret, HapConstants.EventsSalt, HapConstants.EventsWriteInfo)),
            new("HKDF Events-Read", Hkdf(sharedSecret, HapConstants.EventsSalt, HapConstants.EventsReadInfo)),

            // Salted with the AirPlay stream label, the way the mirror cipher's
            // own key derivation is.
            new("SHA-512(\"AirPlayStreamKey\" ‖ secret)",
                SHA512.HashData([.. Encoding.ASCII.GetBytes("AirPlayStreamKey"), .. sharedSecret])[..32]),
        ];
    }

    /// <summary>
    /// The modern data-stream derivations, keyed to a specific stream's
    /// connection ID. Video is proven to use this shape
    /// (HKDF-SHA512 with a "DataStream-Salt&lt;id&gt;" salt), and a mirror's
    /// companion audio sets the same
    /// <c>streamConnectionKeyUseStreamEncryptionKey</c> flag against its OWN
    /// connection ID — so the same derivation with that ID is the first thing
    /// worth trying. ChaCha20-Poly1305's tag makes it self-verifying.
    /// </summary>
    public static IReadOnlyList<StreamKeyCandidate> ForDataStream(byte[] sharedSecret, ulong streamConnectionId)
    {
        if (sharedSecret.Length == 0 || streamConnectionId == 0)
        {
            return [];
        }

        return
        [
            new($"HKDF DataStream-Output (id {streamConnectionId})",
                Hkdf(sharedSecret, $"DataStream-Salt{streamConnectionId}", "DataStream-Output-Encryption-Key")),
            new($"HKDF DataStream-Input (id {streamConnectionId})",
                Hkdf(sharedSecret, $"DataStream-Salt{streamConnectionId}", "DataStream-Input-Encryption-Key")),
        ];
    }

    /// <summary>A single known key, for the ordinary case where the SETUP named one.</summary>
    public static IReadOnlyList<StreamKeyCandidate> Single(byte[] key) => [new("shk from SETUP", key)];

    private static byte[] Hkdf(byte[] secret, string salt, string info) =>
        PairingCrypto.HkdfSha512(secret, salt, info, 32);
}
