namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// Client side of HomeKit <b>transient</b> pair-setup — what our sender runs
/// against a HomePod. Transient pairing is pure SRP with the fixed PIN and no
/// long-term keys: after the four SRP messages both ends hold the same SRP
/// session secret, from which the encrypted-channel and audio keys are derived.
///
/// Flow (controller ⇄ accessory):
///   M1 → State 1, Method PairSetup, Flags Transient
///   M2 ← State 2, PublicKey B, Salt
///   M3 → State 3, PublicKey A, Proof M1
///   M4 ← State 4, Proof M2
/// </summary>
public sealed class TransientPairSetupClient
{
    private readonly HapSrpClient _srp = new();

    /// <summary>The 64-byte transient shared secret (K = H(S)) after a successful exchange.</summary>
    public byte[]? SharedSecret { get; private set; }

    /// <summary>
    /// M1: request transient setup. Item order and the single-byte transient
    /// flag match what real AirPlay 2 devices expect (per pyatv): Method,
    /// State, Flags=0x10.
    /// </summary>
    public byte[] BuildM1() => new Tlv8()
        .Add(TlvType.Method, (byte)PairingMethod.PairSetup)
        .AddState(1)
        .Add(TlvType.Flags, (byte)PairingFlags.Transient)
        .Encode();

    /// <summary>Consume M2 (B + salt), produce M3 (A + client proof).</summary>
    public byte[] HandleM2BuildM3(byte[] m2Body)
    {
        var m2 = Tlv8.Decode(m2Body);
        ThrowOnError(m2, expectedState: 2);
        var salt = m2.Get(TlvType.Salt) ?? throw new InvalidOperationException("pair-setup M2 missing salt");
        var b = m2.Get(TlvType.PublicKey) ?? throw new InvalidOperationException("pair-setup M2 missing server public key");

        var proof = _srp.ComputeProof(salt, b, Srp6.Username, HapConstants.TransientPin);

        return new Tlv8()
            .AddState(3)
            .Add(TlvType.PublicKey, _srp.PublicA)
            .Add(TlvType.Proof, proof)
            .Encode();
    }

    /// <summary>
    /// Consume M4; on success the shared secret is available. The device
    /// having accepted M3 already proves the exchange; we verify the server
    /// proof when present (some devices omit it in transient mode, like pyatv
    /// tolerates), and always fail on an explicit error.
    /// </summary>
    public void HandleM4(byte[] m4Body)
    {
        var m4 = Tlv8.Decode(m4Body);
        ThrowOnError(m4, expectedState: 4);
        if (m4.Get(TlvType.Proof) is { } serverProof && !_srp.VerifyServerProof(serverProof))
        {
            throw new InvalidOperationException("pair-setup M4 server proof invalid (device rejected the PIN?)");
        }

        SharedSecret = _srp.SessionKey;
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
