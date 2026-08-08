namespace Streamsemble.AirPlay.Common.FairPlay;

/// <summary>
/// Unwraps the 16-byte stream key an AirPlay sender hides in a SETUP's
/// <c>ekey</c> field, given the <c>fp-setup</c> phase-2 message that keyed it.
/// </summary>
public interface IFairPlayKeyDecryptor
{
    /// <param name="keyMessage">
    /// The 164-byte <c>POST /fp-setup</c> phase-2 request body, retained from
    /// the handshake — <c>ekey</c> is only meaningful against it.
    /// </param>
    /// <param name="ekey">The 72-byte wrapped key record from the stream SETUP.</param>
    /// <returns>The raw 16-byte stream key.</returns>
    byte[] UnwrapStreamKey(ReadOnlySpan<byte> keyMessage, ReadOnlySpan<byte> ekey);
}

/// <summary>
/// The process-wide FairPlay key unwrapper. <see cref="FairPlayKeyDecryptor"/>
/// serves by default, so a sender that keys its stream the legacy way works
/// without any wiring; <see cref="Register"/> exists so a future protocol
/// revision can be dropped in without touching the receiver.
/// </summary>
public static class FairPlayKeys
{
    private static IFairPlayKeyDecryptor _decryptor = new FairPlayKeyDecryptor();

    /// <summary>Replaces the decryptor for the process. Last registration wins.</summary>
    public static void Register(IFairPlayKeyDecryptor decryptor) => Volatile.Write(ref _decryptor, decryptor);

    /// <summary>
    /// Unwraps a stream key. Failures throw rather than returning something
    /// unusable: a sender told no renegotiates or gives up cleanly, whereas one
    /// whose key we got wrong streams megabytes of undecryptable video while
    /// both ends report success.
    /// </summary>
    public static byte[] UnwrapStreamKey(ReadOnlySpan<byte> keyMessage, ReadOnlySpan<byte> ekey)
    {
        if (keyMessage.Length != 164)
        {
            throw new ArgumentException(
                $"fp-setup phase 2 message must be 164 bytes, got {keyMessage.Length}", nameof(keyMessage));
        }

        var key = Volatile.Read(ref _decryptor).UnwrapStreamKey(keyMessage, ekey);
        return key.Length == 16
            ? key
            : throw new InvalidOperationException($"FairPlay decryptor returned {key.Length} bytes, expected 16");
    }
}
