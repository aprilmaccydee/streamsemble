using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// SRP-6a as HAP uses it: SHA-512, the 3072-bit RFC 5054 group, username
/// "Pair-Setup". We need both roles — the receiver is the SRP server
/// (accessory), the HomePod sender is the SRP client (controller).
/// </summary>
public static class Srp6
{
    public const string Username = "Pair-Setup";

    private static readonly Srp6GroupParameters Group = Org.BouncyCastle.Crypto.Agreement.Srp.Srp6StandardGroups.rfc5054_3072;

    public static byte[] ToBytes(BigInteger value)
    {
        // Unsigned, big-endian, no sign byte.
        var bytes = value.ToByteArrayUnsigned();
        return bytes;
    }

    public static BigInteger ToInt(byte[] bytes) => new(1, bytes);

    /// <summary>SRP server (accessory / receiver). Holds the password, issues salt + B, verifies the client proof.</summary>
    public sealed class Server
    {
        private readonly Srp6Server _server = new();
        private readonly Srp6VerifierGenerator _verifierGen = new();
        private BigInteger? _secret;
        public byte[] Salt { get; }
        public byte[] Verifier { get; }

        public Server(string password, byte[]? salt = null)
        {
            Salt = salt ?? new SecureRandom().GenerateSeed(16);
            _verifierGen.Init(Group, new Sha512Digest());
            var v = _verifierGen.GenerateVerifier(Salt, System.Text.Encoding.UTF8.GetBytes(Username), System.Text.Encoding.UTF8.GetBytes(password));
            Verifier = ToBytes(v);
            _server.Init(Group, v, new Sha512Digest(), new SecureRandom());
        }

        /// <summary>Server public value B.</summary>
        public byte[] GenerateB() => ToBytes(_server.GenerateServerCredentials());

        /// <summary>Consume client A and compute the shared premaster S.</summary>
        public void ReceiveA(byte[] a) => _secret = _server.CalculateSecret(ToInt(a));

        /// <summary>Verify client proof M1; throws on mismatch.</summary>
        public void VerifyClientProof(byte[] m1)
        {
            if (!_server.VerifyClientEvidenceMessage(ToInt(m1)))
            {
                throw new InvalidOperationException("SRP client proof (M1) verification failed");
            }
        }

        /// <summary>Server proof M2, sent back to the client.</summary>
        public byte[] ComputeServerProof() => ToBytes(_server.CalculateServerEvidenceMessage());

        /// <summary>Shared session key S (raw premaster secret bytes).</summary>
        public byte[] SessionKey() => ToBytes(_secret ?? throw new InvalidOperationException("SRP secret not computed (call ReceiveA first)"));
    }

    /// <summary>SRP client (controller / HomePod sender). Knows username + password, computes A + M1.</summary>
    public sealed class Client
    {
        private readonly Srp6Client _client = new();
        private BigInteger? _secret;

        public Client() => _client.Init(Group, new Sha512Digest(), new SecureRandom());

        /// <summary>
        /// Client public value A. In SRP-6a A is independent of the password,
        /// but BouncyCastle folds identity+salt+password into one call, so we
        /// pass them here (they only affect the internally-stored x, used later
        /// for S and M1) and A comes out deterministically from the client's
        /// random a.
        /// </summary>
        public byte[] GenerateA(byte[] salt, string password)
            => ToBytes(_client.GenerateClientCredentials(salt,
                System.Text.Encoding.UTF8.GetBytes(Username),
                System.Text.Encoding.UTF8.GetBytes(password)));

        public void ReceiveB(byte[] b) => _secret = _client.CalculateSecret(ToInt(b));

        public byte[] ComputeClientProof() => ToBytes(_client.CalculateClientEvidenceMessage());

        public bool VerifyServerProof(byte[] m2) => _client.VerifyServerEvidenceMessage(ToInt(m2));

        public byte[] SessionKey() => ToBytes(_secret ?? throw new InvalidOperationException("SRP secret not computed (call ReceiveB first)"));
    }
}
