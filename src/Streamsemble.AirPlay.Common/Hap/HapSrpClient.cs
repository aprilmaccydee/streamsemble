using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// SRP-6a client exactly as HomeKit / AirPlay 2 devices expect it (SHA-512,
/// 3072-bit RFC 5054 group, username "Pair-Setup"). This matches the
/// srptools library pyatv uses — crucially, the session key is <c>K = H(S)</c>
/// (64 bytes) and the proof is
/// <c>M1 = H(H(N)⊕H(g) | H(user) | s | A | B | K)</c>. BouncyCastle's built-in
/// SRP uses different (non-HAP) formulas, so it cannot interop with real
/// devices; hence this hand-rolled version over BouncyCastle's big-integer math.
/// </summary>
public sealed class HapSrpClient
{
    private static readonly Srp6GroupParameters Group = Srp6StandardGroups.rfc5054_3072;

    private readonly BigInteger _n = Group.N;
    private readonly BigInteger _g = Group.G;
    private readonly BigInteger _a;   // client private ephemeral
    private readonly BigInteger _aPub; // A = g^a mod N
    private byte[]? _sessionKey;       // K = H(S), 64 bytes — the transient shared secret
    private byte[]? _m1;

    public HapSrpClient()
    {
        var seed = new byte[32];
        new SecureRandom().NextBytes(seed);
        _a = new BigInteger(1, seed).Mod(_n);
        _aPub = _g.ModPow(_a, _n);
    }

    private HapSrpClient(byte[] fixedPrivate, bool _)
    {
        _a = new BigInteger(1, fixedPrivate).Mod(_n);
        _aPub = _g.ModPow(_a, _n);
    }

    /// <summary>Test-only: fix the client private ephemeral for reproducible vectors.</summary>
    public static HapSrpClient ForTesting(byte[] fixedPrivate) => new(fixedPrivate, false);

    /// <summary>Client public value A (raw big-endian).</summary>
    public byte[] PublicA => IntToBytes(_aPub);

    /// <summary>Session key K = SHA-512(S), 64 bytes; the transient shared secret.</summary>
    public byte[] SessionKey => _sessionKey ?? throw new InvalidOperationException("SRP not completed");

    /// <summary>Given the device's salt and B, compute the client proof M1 (and derive K).</summary>
    public byte[] ComputeProof(byte[] salt, byte[] serverB, string username = "Pair-Setup", string password = "3939")
    {
        var s = new BigInteger(1, salt);
        var bPub = new BigInteger(1, serverB);

        // k = H(N | PAD(g))
        var k = HashInt(IntToBytes(_n), Pad(_g));

        // x = H(s | H(user | ":" | password))
        var inner = Sha512(Encoding.UTF8.GetBytes($"{username}:{password}"));
        var x = HashInt(IntToBytes(s), inner);

        // u = H(PAD(A) | PAD(B))
        var u = HashInt(Pad(_aPub), Pad(bPub));

        // S = (B - k*g^x) ^ (a + u*x) mod N
        var v = _g.ModPow(x, _n);
        var baseVal = bPub.Subtract(k.Multiply(v)).Mod(_n);
        var s2 = baseVal.ModPow(_a.Add(u.Multiply(x)), _n);

        _sessionKey = Sha512(IntToBytes(s2)); // K = H(S)

        // M1 = H( H(N) XOR H(g) | H(user) | s | A | B | K )
        var hN = HashInt(IntToBytes(_n));
        var hG = HashInt(IntToBytes(_g));
        var hUser = HashInt(Encoding.UTF8.GetBytes(username));
        _m1 = Sha512(Concat(
            IntToBytes(hN.Xor(hG)),
            IntToBytes(hUser),
            IntToBytes(s),
            IntToBytes(_aPub),
            IntToBytes(bPub),
            _sessionKey));
        return _m1;
    }

    /// <summary>Verify the device's proof M2 = H(A | M1 | K).</summary>
    public bool VerifyServerProof(byte[] m2)
    {
        if (_m1 is null || _sessionKey is null)
        {
            return false;
        }

        var expected = Sha512(Concat(IntToBytes(_aPub), _m1, _sessionKey));
        return CryptographicOperations.FixedTimeEquals(expected, m2);
    }

    private static byte[] Sha512(byte[] data)
    {
        var digest = new Sha512Digest();
        digest.BlockUpdate(data, 0, data.Length);
        var output = new byte[64];
        digest.DoFinal(output, 0);
        return output;
    }

    private static BigInteger HashInt(params byte[][] parts) => new(1, Sha512(Concat(parts)));

    // Minimal big-endian magnitude, matching srptools' int_to_bytes.
    private static byte[] IntToBytes(BigInteger v) => v.ToByteArrayUnsigned();

    private byte[] Pad(BigInteger v)
    {
        var bytes = IntToBytes(v);
        var width = IntToBytes(_n).Length;
        if (bytes.Length >= width)
        {
            return bytes;
        }

        var padded = new byte[width];
        Array.Copy(bytes, 0, padded, width - bytes.Length, bytes.Length);
        return padded;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
