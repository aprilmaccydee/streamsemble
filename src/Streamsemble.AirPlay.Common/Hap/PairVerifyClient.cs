using System.Text;

namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// Client side of HomeKit <b>pair-verify</b>: a Curve25519 station-to-station
/// exchange that re-establishes a session with an accessory we already paired
/// with (via <see cref="PairSetupClient"/>), proving both identities with the
/// stored long-term Ed25519 keys. This is what verified AirPlay 2 receivers
/// (TVs) require on every connect before they will stream buffered audio.
///
///   M1 → State 1, PublicKey (our ephemeral X25519)
///   M2 ← State 2, PublicKey (accessory X25519), EncryptedData{ AccId, Sig }
///   M3 → State 3, EncryptedData{ ControllerId, Sig }
///   M4 ← State 4 (success)
///
/// The resulting X25519 shared secret feeds <see cref="HapSessionKeys.Derive"/>
/// for the encrypted control/events channels and the audio key.
/// </summary>
public sealed class PairVerifyClient(HomeKitPairingStore store, HomeKitPairingStore.Accessory accessory)
{
    private byte[] _ourPrivate = [];
    private byte[] _ourPublic = [];
    private byte[] _accessoryPublic = [];
    private byte[] _sessionKey = [];

    /// <summary>The X25519 shared secret, valid after <see cref="HandleM2BuildM3"/>.</summary>
    public byte[] SharedSecret { get; private set; } = [];

    public byte[] BuildM1()
    {
        (_ourPrivate, _ourPublic) = PairingCrypto.GenerateX25519();
        return new Tlv8()
            .AddState(1)
            .Add(TlvType.PublicKey, _ourPublic)
            .Encode();
    }

    public byte[] HandleM2BuildM3(byte[] m2Body)
    {
        var m2 = Tlv8.Decode(m2Body);
        ThrowOnError(m2, expectedState: 2);
        _accessoryPublic = m2.Get(TlvType.PublicKey) ?? throw new InvalidOperationException("pair-verify M2 missing accessory public key");
        var encrypted = m2.Get(TlvType.EncryptedData) ?? throw new InvalidOperationException("pair-verify M2 missing encrypted data");

        SharedSecret = PairingCrypto.X25519SharedSecret(_ourPrivate, _accessoryPublic);
        _sessionKey = PairingCrypto.HkdfSha512(SharedSecret,
            HapConstants.PairVerifyEncryptSalt, HapConstants.PairVerifyEncryptInfo, 32);

        var decrypted = PairingCrypto.ChaCha20Poly1305Decrypt(
            _sessionKey, PairingCrypto.Nonce(HapConstants.NoncePairVerifyMsg2), encrypted);
        var sub = Tlv8.Decode(decrypted);
        var accessoryId = sub.Get(TlvType.Identifier) ?? throw new InvalidOperationException("pair-verify M2 missing accessory id");
        var accessorySig = sub.Get(TlvType.Signature) ?? throw new InvalidOperationException("pair-verify M2 missing accessory signature");

        // Verify the accessory signed accessoryPub ‖ accessoryId ‖ ourPub with its stored LTPK.
        var accessoryInfo = Concat(_accessoryPublic, accessoryId, _ourPublic);
        var accessoryLtpk = Convert.FromBase64String(accessory.AccessoryLtpk);
        if (!PairingCrypto.Ed25519Verify(accessoryLtpk, accessoryInfo, accessorySig))
        {
            throw new InvalidOperationException("pair-verify M2 accessory signature invalid (stale/incorrect stored key — re-pair)");
        }

        // M3: prove our identity — sign ourPub ‖ controllerId ‖ accessoryPub.
        var controllerId = Encoding.UTF8.GetBytes(store.ControllerId);
        var deviceInfo = Concat(_ourPublic, controllerId, _accessoryPublic);
        var signature = PairingCrypto.Ed25519Sign(store.ControllerLtsk, deviceInfo);

        var subTlv = new Tlv8()
            .Add(TlvType.Identifier, controllerId)
            .Add(TlvType.Signature, signature)
            .Encode();
        var encrypted3 = PairingCrypto.ChaCha20Poly1305Encrypt(
            _sessionKey, PairingCrypto.Nonce(HapConstants.NoncePairVerifyMsg3), subTlv);

        return new Tlv8()
            .AddState(3)
            .Add(TlvType.EncryptedData, encrypted3)
            .Encode();
    }

    public void HandleM4(byte[] m4Body)
    {
        var m4 = Tlv8.Decode(m4Body);
        ThrowOnError(m4, expectedState: 4);
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
            throw new InvalidOperationException($"pair-verify rejected: {(PairingError)error}");
        }

        if (tlv.GetByte(TlvType.State) is { } state && state != expectedState)
        {
            throw new InvalidOperationException($"pair-verify state mismatch: expected {expectedState}, got {state}");
        }
    }
}
