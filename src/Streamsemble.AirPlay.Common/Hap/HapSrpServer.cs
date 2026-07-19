using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// SRP-6a server (accessory role) with the exact HAP formulas that
/// <see cref="HapSrpClient"/> implements for the controller role: SHA-512,
/// 3072-bit RFC 5054 group, K = H(S),
/// M1 = H(H(N)⊕H(g) | H(user) | s | A | B | K), M2 = H(A | M1 | K).
/// BouncyCastle's built-in SRP server (<see cref="Srp6.Server"/>) uses
/// different formulas and cannot verify a real macOS/iOS sender's proof —
/// this class is what the M3 receiver pairs with.
/// </summary>
public sealed class HapSrpServer
{
    private static readonly Srp6GroupParameters Group = Srp6StandardGroups.rfc5054_3072;

    private readonly BigInteger _n = Group.N;
    private readonly BigInteger _g = Group.G;
    private readonly BigInteger _v;    // password verifier v = g^x mod N
    private readonly BigInteger _b;    // server private ephemeral
    private readonly BigInteger _bPub; // B = k*v + g^b mod N
    private byte[]? _sessionKey;       // K = H(S), 64 bytes
    private byte[]? _clientM1;

    public HapSrpServer(string password, string username = Srp6.Username, byte[]? salt = null, byte[]? fixedPrivate = null)
    {
        // Regenerate a random salt while its minimal big-endian form would
        // lose a leading zero byte: both sides hash the salt as an integer
        // magnitude (srptools convention), so a full-width salt removes any
        // ambiguity with senders that hash the raw octets instead.
        if (salt is null)
        {
            var random = new SecureRandom();
            do
            {
                salt = random.GenerateSeed(16);
            }
            while (salt[0] == 0);
        }

        Salt = salt;

        // x = H(s | H(user ":" password)); v = g^x mod N
        var inner = Sha512(Encoding.UTF8.GetBytes($"{username}:{password}"));
        var x = HashInt(IntToBytes(new BigInteger(1, Salt)), inner);
        _v = _g.ModPow(x, _n);

        // k = H(N | PAD(g))
        var k = HashInt(IntToBytes(_n), Pad(_g));

        // B = k*v + g^b mod N; keep B full-width (no leading-zero loss on the
        // wire) so clients that hash B as transmitted agree with our M1 math.
        var random2 = new SecureRandom();
        do
        {
            byte[] seed;
            if (fixedPrivate is not null)
            {
                seed = fixedPrivate;
            }
            else
            {
                seed = new byte[32];
                random2.NextBytes(seed);
            }

            _b = new BigInteger(1, seed).Mod(_n);
            _bPub = k.Multiply(_v).Add(_g.ModPow(_b, _n)).Mod(_n);
        }
        while (fixedPrivate is null && IntToBytes(_bPub).Length != IntToBytes(_n).Length);
    }

    public byte[] Salt { get; }

    /// <summary>Server public value B (raw big-endian).</summary>
    public byte[] PublicB => IntToBytes(_bPub);

    /// <summary>Session key K = SHA-512(S), 64 bytes; valid after <see cref="VerifyClientProof"/>.</summary>
    public byte[] SessionKey => _sessionKey ?? throw new InvalidOperationException("SRP not completed");

    /// <summary>
    /// Given the client's A and proof M1, compute S/K and check the proof.
    /// Returns false (without throwing) on mismatch so callers can reply with
    /// a proper pairing error TLV.
    /// </summary>
    public bool VerifyClientProof(byte[] clientA, byte[] clientM1, string username = Srp6.Username)
    {
        var aPub = new BigInteger(1, clientA);
        if (aPub.Mod(_n).SignValue == 0)
        {
            return false; // A ≡ 0 mod N is the classic SRP poison value
        }

        // u = H(PAD(A) | PAD(B)); S = (A * v^u)^b mod N; K = H(S)
        var u = HashInt(Pad(aPub), Pad(_bPub));
        var s2 = aPub.Multiply(_v.ModPow(u, _n)).Mod(_n).ModPow(_b, _n);
        _sessionKey = Sha512(IntToBytes(s2));

        // Expected M1 = H( H(N) XOR H(g) | H(user) | s | A | B | K )
        var hN = HashInt(IntToBytes(_n));
        var hG = HashInt(IntToBytes(_g));
        var hUser = HashInt(Encoding.UTF8.GetBytes(username));
        var expected = Sha512(Concat(
            IntToBytes(hN.Xor(hG)),
            IntToBytes(hUser),
            IntToBytes(new BigInteger(1, Salt)),
            IntToBytes(aPub),
            IntToBytes(_bPub),
            _sessionKey));

        if (!CryptographicOperations.FixedTimeEquals(expected, clientM1))
        {
            _sessionKey = null;
            return false;
        }

        _clientM1 = clientM1;
        _aPubBytes = IntToBytes(aPub);
        return true;
    }

    private byte[]? _aPubBytes;

    /// <summary>Server proof M2 = H(A | M1 | K), sent back in M4.</summary>
    public byte[] ComputeServerProof()
    {
        if (_clientM1 is null || _sessionKey is null || _aPubBytes is null)
        {
            throw new InvalidOperationException("client proof not verified yet");
        }

        return Sha512(Concat(_aPubBytes, _clientM1, _sessionKey));
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

    // Minimal big-endian magnitude, matching srptools' int_to_bytes and HapSrpClient.
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
