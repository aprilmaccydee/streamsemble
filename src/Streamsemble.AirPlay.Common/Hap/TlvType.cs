namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>HAP (HomeKit) pairing TLV8 item types, per pair_ap / HAP spec §5.</summary>
public enum TlvType : byte
{
    Method = 0x00,
    Identifier = 0x01,
    Salt = 0x02,
    PublicKey = 0x03,
    Proof = 0x04,
    EncryptedData = 0x05,
    State = 0x06,
    Error = 0x07,
    RetryDelay = 0x08,
    Certificate = 0x09,
    Signature = 0x0a,
    Permissions = 0x0b,
    FragmentData = 0x0c,
    FragmentLast = 0x0d,
    Flags = 0x13,
    Separator = 0xff,
}

/// <summary>Pairing method values (TLV <see cref="TlvType.Method"/>).</summary>
public enum PairingMethod : byte
{
    PairSetup = 0x00,
    PairSetupWithAuth = 0x01,
    PairVerify = 0x02,
    AddPairing = 0x03,
    RemovePairing = 0x04,
    ListPairings = 0x05,
}

/// <summary>Pairing error codes (TLV <see cref="TlvType.Error"/>).</summary>
public enum PairingError : byte
{
    Unknown = 0x01,
    Authentication = 0x02,
    Backoff = 0x03,
    MaxPeers = 0x04,
    MaxTries = 0x05,
    Unavailable = 0x06,
    Busy = 0x07,
}

/// <summary>Transient-pairing flag (TLV <see cref="TlvType.Flags"/>). This is the only flag pair_ap defines.</summary>
public static class PairingFlags
{
    public const uint Transient = 0x00000010;
}
