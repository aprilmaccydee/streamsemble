namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// Derives the session keys from a completed pairing's shared secret, per
/// OwnTone's <c>session_cipher_setup</c>:
/// <list type="bullet">
/// <item>the encrypted control/RTSP channel keys come from HKDF over the
///   <b>full</b> shared secret (64 bytes for transient);</item>
/// <item>the realtime-audio ChaCha20-Poly1305 key is the <b>first 32 bytes of
///   the shared secret used directly</b> — no HKDF, no salt.</item>
/// </list>
/// </summary>
public sealed class HapSessionKeys
{
    public required byte[] ControlWriteKey { get; init; }
    public required byte[] ControlReadKey { get; init; }
    public required byte[] AudioKey { get; init; }

    /// <summary>Events channel: what the sender writes with (receiver's read key).</summary>
    public required byte[] EventsWriteKey { get; init; }

    /// <summary>Events channel: what the sender reads with (receiver's write key).</summary>
    public required byte[] EventsReadKey { get; init; }

    /// <param name="controllerRole">
    /// True when we are the controller (our sender → a HomePod). The events
    /// channel is a reverse connection, so a receiver would swap read/write;
    /// for the sender the control channel uses write=our-writes.
    /// </param>
    public static HapSessionKeys Derive(byte[] sharedSecret, bool controllerRole = true)
    {
        var write = PairingCrypto.HkdfSha512(sharedSecret, HapConstants.ControlSalt, HapConstants.ControlWriteInfo, 32);
        var read = PairingCrypto.HkdfSha512(sharedSecret, HapConstants.ControlSalt, HapConstants.ControlReadInfo, 32);
        var eventsRead = PairingCrypto.HkdfSha512(sharedSecret, HapConstants.EventsSalt, HapConstants.EventsReadInfo, 32);
        var eventsWrite = PairingCrypto.HkdfSha512(sharedSecret, HapConstants.EventsSalt, HapConstants.EventsWriteInfo, 32);
        return new HapSessionKeys
        {
            ControlWriteKey = controllerRole ? write : read,
            ControlReadKey = controllerRole ? read : write,
            AudioKey = sharedSecret[..32],
            // Info names are receiver-perspective: the sender writes with the
            // "read" key and reads with the "write" key.
            EventsWriteKey = controllerRole ? eventsRead : eventsWrite,
            EventsReadKey = controllerRole ? eventsWrite : eventsRead,
        };
    }
}
