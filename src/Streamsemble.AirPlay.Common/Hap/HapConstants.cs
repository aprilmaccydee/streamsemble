namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// HKDF salt/info strings and AEAD nonces for HAP pairing, matching pair_ap /
/// the HomeKit Accessory Protocol spec so we interop on the wire with iOS and
/// HomePods. All strings are ASCII; keys are 32 bytes unless noted.
/// </summary>
public static class HapConstants
{
    /// <summary>Transient pair-setup PIN (AirPlay uses a fixed PIN, not a displayed one).</summary>
    public const string TransientPin = "3939";

    // pair-setup: symmetric key that protects the M5/M6 exchange.
    public const string PairSetupEncryptSalt = "Pair-Setup-Encrypt-Salt";
    public const string PairSetupEncryptInfo = "Pair-Setup-Encrypt-Info";

    // pair-setup: controller/accessory sign-material key.
    public const string PairSetupControllerSignSalt = "Pair-Setup-Controller-Sign-Salt";
    public const string PairSetupControllerSignInfo = "Pair-Setup-Controller-Sign-Info";
    public const string PairSetupAccessorySignSalt = "Pair-Setup-Accessory-Sign-Salt";
    public const string PairSetupAccessorySignInfo = "Pair-Setup-Accessory-Sign-Info";

    // pair-verify: symmetric key that protects the verify sub-messages.
    public const string PairVerifyEncryptSalt = "Pair-Verify-Encrypt-Salt";
    public const string PairVerifyEncryptInfo = "Pair-Verify-Encrypt-Info";

    // Encrypted RTSP/control channel keys derived from the verify (or transient) shared secret.
    public const string ControlSalt = "Control-Salt";
    public const string ControlReadInfo = "Control-Read-Encryption-Key";
    public const string ControlWriteInfo = "Control-Write-Encryption-Key";

    // Events channel (reverse RTSP: receiver → sender) keys, same shared secret.
    // Names are from the receiver's perspective, so the sender READS with the
    // write-info key and WRITES with the read-info key.
    public const string EventsSalt = "Events-Salt";
    public const string EventsReadInfo = "Events-Read-Encryption-Key";
    public const string EventsWriteInfo = "Events-Write-Encryption-Key";

    // AEAD nonces for individual pairing messages (8-byte ASCII, HAP-padded to 12).
    public const string NoncePairSetupMsg5 = "PS-Msg05";
    public const string NoncePairSetupMsg6 = "PS-Msg06";
    public const string NoncePairVerifyMsg2 = "PV-Msg02";
    public const string NoncePairVerifyMsg3 = "PV-Msg03";
}
