using Streamsemble.AirPlay.Common.FairPlay;

namespace Streamsemble.AirPlay.Common.Video;

/// <summary>
/// The key material a mirror stream SETUP carried, and the one place that
/// decides what it means. A sender keys the video channel one of two ways and
/// says which by what it puts in the plist:
/// <list type="bullet">
/// <item><c>shk</c> — a HAP-paired session, key handed over directly under the
///   protection of the pairing. This is what our advertised feature bits ask
///   for and what both real devices on this network do.</item>
/// <item><c>ekey</c> — the legacy FairPlay path, key wrapped against the
///   <c>fp-setup</c> exchange and unwrapped by
///   <see cref="FairPlayKeyDecryptor"/>.</item>
/// </list>
/// Either way the 16 bytes that come out feed the same
/// <see cref="MirrorAesCtrCipher"/> derivation, so nothing downstream has to
/// care which happened.
/// </summary>
/// <param name="SharedKey">The SETUP's <c>shk</c>, when the session is HAP-keyed.</param>
/// <param name="SessionKey">
/// The key material the HAP pairing already established, used when the SETUP
/// names no key at all. macOS screen mirroring does exactly that — it asks for
/// <c>streamConnectionKeyUseStreamEncryptionKey</c> and expects both sides to
/// derive from the pairing secret, so an absent <c>shk</c> is an instruction
/// rather than an omission.
/// </param>
/// <param name="EncryptedKey">The SETUP's <c>ekey</c>, when the session is FairPlay-keyed.</param>
/// <param name="FairPlayKeyMessage">
/// The retained 164-byte <c>fp-setup</c> phase-2 body. Required to make sense of
/// <paramref name="EncryptedKey"/>; null when the sender never ran fp-setup.
/// </param>
/// <param name="StreamConnectionId">
/// The SETUP's <c>streamConnectionID</c>. It is not decoration — it is hashed
/// into the key and IV, so a wrong or missing value produces a cipher that
/// decrypts to noise.
/// </param>
public sealed record MirrorKeyRequest(
    byte[]? SharedKey,
    byte[]? EncryptedKey,
    byte[]? FairPlayKeyMessage,
    ulong StreamConnectionId,
    byte[]? SessionKey = null)
{
    /// <summary>
    /// Builds the cipher for this stream.
    ///
    /// Both halves of the key material come together here: the FairPlay
    /// <c>ekey</c> if the sender sent one, and the pairing's ECDH secret, hashed
    /// together into the 16 bytes the stream key and IV are then derived from.
    /// A transiently-paired screen mirror sends no <c>ekey</c> at all, so its
    /// material is the pairing secret alone — which is a case of the same
    /// formula, not a different scheme.
    /// </summary>
    public IMirrorStreamCipher Resolve(out MirrorKeySource source)
    {
        // shk, when a sender names one outright, is already the stream key and
        // skips the two-stage derivation entirely.
        if (SharedKey is { Length: >= 16 } shk)
        {
            source = MirrorKeySource.SharedKey;
            return new MirrorAesCtrCipher(shk, StreamConnectionId, source);
        }

        if (SessionKey is not { Length: > 0 } ecdhSecret)
        {
            throw new InvalidDataException(
                "mirror stream SETUP named no key and the session established none — "
                + "nothing to key the video channel with");
        }

        ReadOnlySpan<byte> aesKey = default;
        source = MirrorKeySource.PairingSecret;
        if (EncryptedKey is { Length: > 0 } ekey)
        {
            if (FairPlayKeyMessage is not { } keyMessage)
            {
                throw new InvalidDataException(
                    "the stream SETUP carried a FairPlay ekey but no fp-setup handshake preceded it — "
                    + "there is nothing to unwrap it against");
            }

            aesKey = FairPlayKeys.UnwrapStreamKey(keyMessage, ekey);
            source = MirrorKeySource.FairPlay;
        }

        var material = MirrorAesCtrCipher.DeriveKeyMaterial(aesKey, ecdhSecret);
        return new MirrorAesCtrCipher(material, StreamConnectionId, source);
    }
}
