namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// Accessory (server) side of HomeKit <b>transient</b> pair-setup — what the M3
/// receiver runs when a macOS/iOS sender (or our own
/// <see cref="TransientPairSetupClient"/>) connects. Pure SRP with the fixed
/// PIN and no long-term keys; after M4 both ends hold the 64-byte session
/// secret that <see cref="HapSessionKeys.Derive"/> turns into channel keys
/// (pass <c>controllerRole: false</c> on this side).
///
/// Flow (controller ⇄ accessory):
///   M1 → State 1, Method PairSetup, Flags Transient
///   M2 ← State 2, PublicKey B, Salt
///   M3 → State 3, PublicKey A, Proof M1
///   M4 ← State 4, Proof M2 (or Error Authentication on a bad proof)
/// </summary>
public sealed class TransientPairSetupServer
{
    private readonly HapSrpServer _srp = new(HapConstants.TransientPin);

    /// <summary>The 64-byte transient shared secret (K = H(S)) after a successful exchange.</summary>
    public byte[]? SharedSecret { get; private set; }

    /// <summary>
    /// Dispatch one inbound pair-setup message by its State TLV and produce the
    /// reply body. Unknown/out-of-order states yield an Unknown error TLV.
    /// </summary>
    public byte[] HandleMessage(byte[] body)
    {
        var tlv = Tlv8.Decode(body);
        return tlv.GetByte(TlvType.State) switch
        {
            1 => HandleM1BuildM2(tlv),
            3 => HandleM3BuildM4(tlv),
            _ => ErrorReply(state: 2, PairingError.Unknown),
        };
    }

    private byte[] HandleM1BuildM2(IReadOnlyDictionary<byte, byte[]> m1)
    {
        // Real senders declare Method PairSetup + the transient flag; we accept
        // a missing flag too (our own client always sends it, pyatv tolerates
        // accessories that ignore it) — transient is the only mode we offer.
        return new Tlv8()
            .AddState(2)
            .Add(TlvType.PublicKey, _srp.PublicB)
            .Add(TlvType.Salt, _srp.Salt)
            .Encode();
    }

    private byte[] HandleM3BuildM4(IReadOnlyDictionary<byte, byte[]> m3)
    {
        var a = m3.Get(TlvType.PublicKey);
        var proof = m3.Get(TlvType.Proof);
        if (a is null || proof is null || !_srp.VerifyClientProof(a, proof))
        {
            return ErrorReply(state: 4, PairingError.Authentication);
        }

        SharedSecret = _srp.SessionKey;
        return new Tlv8()
            .AddState(4)
            .Add(TlvType.Proof, _srp.ComputeServerProof())
            .Encode();
    }

    private static byte[] ErrorReply(byte state, PairingError error) => new Tlv8()
        .AddState(state)
        .Add(TlvType.Error, (byte)error)
        .Encode();
}
