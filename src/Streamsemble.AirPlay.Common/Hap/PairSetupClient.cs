using System.Text;

namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// Client side of HomeKit <b>authenticated</b> pair-setup (PIN-based), which
/// establishes a persistent pairing: after the SRP exchange (M1–M4, keyed by
/// the PIN the accessory shows on screen) the controller and accessory swap
/// signed long-term Ed25519 public keys (M5–M6). Unlike transient pairing this
/// leaves the accessory remembering our identity, which verified receivers
/// (TVs) require before they will render buffered audio.
///
///   M1 → State 1, Method PairSetup
///   M2 ← State 2, PublicKey B, Salt
///   M3 → State 3, PublicKey A, Proof M1            (SRP with the on-screen PIN)
///   M4 ← State 4, Proof M2
///   M5 → State 5, EncryptedData{ Id, LTPK, Sig }   (our long-term identity)
///   M6 ← State 6, EncryptedData{ Id, LTPK, Sig }   (accessory long-term identity)
/// </summary>
public sealed class PairSetupClient(HomeKitPairingStore store)
{
    private readonly HapSrpClient _srp = new();
    private byte[] _srpKey = [];
    private byte[] _salt = [];
    private byte[] _serverB = [];

    /// <summary>The accessory's pairing id + long-term public key, valid after <see cref="HandleM6"/>.</summary>
    public string AccessoryId { get; private set; } = "";
    public byte[] AccessoryLtpk { get; private set; } = [];

    // M1 (Method PairSetup) carries no PIN; per pyatv it must be sent right
    // after /pair-pin-start so the salt/B are captured while the accessory's
    // pairing session is fresh. The PIN-dependent proof (M3) is deferred so the
    // operator can read the on-screen code without the session going stale.
    public byte[] BuildM1() => new Tlv8()
        .Add(TlvType.Method, (byte)PairingMethod.PairSetup)
        .AddState(1)
        .Encode();

    /// <summary>Capture the accessory's SRP salt + public key (B) from M2.</summary>
    public void HandleM2(byte[] m2Body)
    {
        var m2 = Tlv8.Decode(m2Body);
        ThrowOnError(m2, expectedState: 2);
        _salt = m2.Get(TlvType.Salt) ?? throw new InvalidOperationException("pair-setup M2 missing salt");
        _serverB = m2.Get(TlvType.PublicKey) ?? throw new InvalidOperationException("pair-setup M2 missing server public key");
    }

    /// <summary>Build M3 (A + proof) once the PIN is known.</summary>
    public byte[] BuildM3(string pin)
    {
        var proof = _srp.ComputeProof(_salt, _serverB, Srp6.Username, pin);
        _srpKey = _srp.SessionKey;

        return new Tlv8()
            .AddState(3)
            .Add(TlvType.PublicKey, _srp.PublicA)
            .Add(TlvType.Proof, proof)
            .Encode();
    }

    /// <summary>Verify M4's server proof, then build M5 carrying our signed long-term identity.</summary>
    public byte[] HandleM4BuildM5(byte[] m4Body)
    {
        var m4 = Tlv8.Decode(m4Body);
        ThrowOnError(m4, expectedState: 4);
        if (m4.Get(TlvType.Proof) is { } serverProof && !_srp.VerifyServerProof(serverProof))
        {
            throw new InvalidOperationException("pair-setup M4 server proof invalid (wrong PIN?)");
        }

        var controllerId = Encoding.UTF8.GetBytes(store.ControllerId);
        var controllerLtpk = store.ControllerLtpk;

        // Sign iOSDeviceInfo = iOSDeviceX ‖ ControllerId ‖ ControllerLTPK with our LTSK.
        var deviceX = PairingCrypto.HkdfSha512(_srpKey,
            HapConstants.PairSetupControllerSignSalt, HapConstants.PairSetupControllerSignInfo, 32);
        var deviceInfo = Concat(deviceX, controllerId, controllerLtpk);
        var signature = PairingCrypto.Ed25519Sign(store.ControllerLtsk, deviceInfo);

        var subTlv = new Tlv8()
            .Add(TlvType.Identifier, controllerId)
            .Add(TlvType.PublicKey, controllerLtpk)
            .Add(TlvType.Signature, signature)
            .Encode();

        var encryptKey = PairingCrypto.HkdfSha512(_srpKey,
            HapConstants.PairSetupEncryptSalt, HapConstants.PairSetupEncryptInfo, 32);
        var encrypted = PairingCrypto.ChaCha20Poly1305Encrypt(
            encryptKey, PairingCrypto.Nonce(HapConstants.NoncePairSetupMsg5), subTlv);

        return new Tlv8()
            .AddState(5)
            .Add(TlvType.EncryptedData, encrypted)
            .Encode();
    }

    /// <summary>Decrypt M6, verify the accessory's signature, and record its long-term key.</summary>
    public void HandleM6(byte[] m6Body)
    {
        var m6 = Tlv8.Decode(m6Body);
        ThrowOnError(m6, expectedState: 6);
        var encrypted = m6.Get(TlvType.EncryptedData) ?? throw new InvalidOperationException("pair-setup M6 missing encrypted data");

        var encryptKey = PairingCrypto.HkdfSha512(_srpKey,
            HapConstants.PairSetupEncryptSalt, HapConstants.PairSetupEncryptInfo, 32);
        var decrypted = PairingCrypto.ChaCha20Poly1305Decrypt(
            encryptKey, PairingCrypto.Nonce(HapConstants.NoncePairSetupMsg6), encrypted);

        var sub = Tlv8.Decode(decrypted);
        var accessoryId = sub.Get(TlvType.Identifier) ?? throw new InvalidOperationException("pair-setup M6 missing accessory id");
        var accessoryLtpk = sub.Get(TlvType.PublicKey) ?? throw new InvalidOperationException("pair-setup M6 missing accessory public key");
        var accessorySig = sub.Get(TlvType.Signature) ?? throw new InvalidOperationException("pair-setup M6 missing accessory signature");

        var accessoryX = PairingCrypto.HkdfSha512(_srpKey,
            HapConstants.PairSetupAccessorySignSalt, HapConstants.PairSetupAccessorySignInfo, 32);
        var accessoryInfo = Concat(accessoryX, accessoryId, accessoryLtpk);
        if (!PairingCrypto.Ed25519Verify(accessoryLtpk, accessoryInfo, accessorySig))
        {
            throw new InvalidOperationException("pair-setup M6 accessory signature invalid");
        }

        AccessoryId = Encoding.UTF8.GetString(accessoryId);
        AccessoryLtpk = accessoryLtpk;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static void ThrowOnError(IReadOnlyDictionary<byte, byte[]> tlv, byte expectedState)
    {
        if (tlv.GetByte(TlvType.Error) is { } error)
        {
            throw new InvalidOperationException($"pair-setup rejected: {(PairingError)error}");
        }

        if (tlv.GetByte(TlvType.State) is { } state && state != expectedState)
        {
            throw new InvalidOperationException($"pair-setup state mismatch: expected {expectedState}, got {state}");
        }
    }
}
