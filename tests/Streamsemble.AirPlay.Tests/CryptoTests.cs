using Streamsemble.AirPlay.Common.Hap;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class CryptoTests
{
    [Fact]
    public void Srp6ClientAndServerAgreeOnKeyAndProofs()
    {
        // The receiver (server) and a HomePod-style sender (client) run the HAP
        // transient SRP exchange with username "Pair-Setup" and PIN "3939".
        const string pin = "3939";
        var server = new Srp6.Server(pin);
        var client = new Srp6.Client();

        var b = server.GenerateB();
        var a = client.GenerateA(server.Salt, pin);

        server.ReceiveA(a);
        client.ReceiveB(b);

        var m1 = client.ComputeClientProof();
        server.VerifyClientProof(m1); // throws on mismatch

        var m2 = server.ComputeServerProof();
        Assert.True(client.VerifyServerProof(m2));

        Assert.Equal(server.SessionKey(), client.SessionKey());
    }

    [Fact]
    public void HapSrpClientMatchesSrptoolsReferenceVectors()
    {
        // Reference values computed with the srptools library pyatv uses
        // (SHA-512, 3072-bit group, user "Pair-Setup", pin "3939") for a fixed
        // client private a, salt and server B. Byte-exact match proves wire
        // interop with real AirPlay 2 devices.
        var privateA = Convert.FromHexString(new string('1', 64)); // 0x11 * 32
        var salt = Convert.FromHexString("aabbccddeeff00112233445566778899");
        var expectedA = "c95341372ca4d0b469f87c0ae37bda635713223b577ef5e8d64a959a54e5395722fea50e71bbd44306055760924a64885805ae13545fcfc7e65f4e7b75c765a112d267f79bffb8353d96ae81cfa64ca7eb687a5dba4cf223b21e1362489ca3e056254b25f610c00643490ea19944f6d3d0872bdcc5fc338bebc8936f30b695924e117f364a4cfc3898eeaa1a4bb98957df8eb046eba3cf42558138ca616a1dc7991426292dd258693bbd4c11816e3064e4a7c536f7255ae61735db25e3cf0349eb8b3e9848d0aa572b0f809d8983f1b280581aba96104422fcf088b698f8d899115a5653cc473e87f5e45552a5a126c8b7fc273bd2cd12bfb15ff3501a572b17608f16c33f6e971081f3af1be37a0a96748f60d204e935f6a5d2a71303aea434b45bcb9ab6b4444208d3fdf7a00c07a3ba2a09b56b578170c78ba1573ca2692406ba8d461e43bb3d160f0024a388d7234236537e29c3604f666c69d7244eae29a0e8b9fa5ee490890e5892973fa535707fbe8f06e90dcbb99c7b39b5eb2b45fb";
        var serverB = "5a97a19b413d8b43cff26e45e2bdbf5158fa9346a6761dc0ad4313fa48391208b32543f3f365a15c137cfd5070befbdd2ca5931d0f797204cc18ab3a2139bfa5a33d0705158b4b7be385a47e98bb3a2ce1993ac28dda51d963c87a16b77251d280f1ea0e779330ddd9a15c18962b32f3a251f563eee8a5ba69224f4abd376041ad8bb37bdba4211c186b7ac7b11cbb7141a4129cdb2c44a144f460d015d82e923af84be7ef9e1c1de59779867b2f6d8f1a5114629842791113d3d6f345109be58f49c71361bdf2de8bc440f40a1cc90b8c2e7b291c771c7b26ed7a9e230d8531045be7a5b4f46ddad5bc59302624d92d44a115047b7616c20165eb5bf033f6834dceaaffffec1299c8fbc668d96e1f4a09e64172f2aa3a308e2053596d47d2b9015b29ed5199368a01eb9d62d7d135b1d84d57622abad5e85306c80356a29139fe47c26d4ea9fc61528efbe66d0396828b720d57f6539b625f57646a843d0180472eadbc9eb29416407afdf7afb0a6bbb6b4b392bfeb7f5a1fbf95db5bf398bc";
        var expectedK = "a3c2c45ff876e06106ba03afe90ce4e87af0a92ecd37e06b6068140d869700ee39e9a681bdead14f61fcb436ad3c46832b0e138351e0540f0a1bee2b587ae933";
        var expectedM1 = "b9726a8c098b7f06ffe53dd26a098228e6138332c01103eeac1b1461c2bb39bb161df33bc490a7eebda50c908c70bafa69fd92a99b161fbf96988070c86ecbeb";

        var client = HapSrpClient.ForTesting(privateA);
        Assert.Equal(expectedA, Convert.ToHexString(client.PublicA).ToLowerInvariant());

        var m1 = client.ComputeProof(salt, Convert.FromHexString(serverB));
        Assert.Equal(expectedK, Convert.ToHexString(client.SessionKey).ToLowerInvariant());
        Assert.Equal(expectedM1, Convert.ToHexString(m1).ToLowerInvariant());
    }

    [Fact]
    public void Srp6WrongPinFailsClientProof()
    {
        var server = new Srp6.Server("3939");
        var client = new Srp6.Client();
        var b = server.GenerateB();
        var a = client.GenerateA(server.Salt, "0000");
        server.ReceiveA(a);
        client.ReceiveB(b);

        Assert.Throws<InvalidOperationException>(() => server.VerifyClientProof(client.ComputeClientProof()));
    }

    [Fact]
    public void X25519SharedSecretMatchesBothWays()
    {
        var (aPriv, aPub) = PairingCrypto.GenerateX25519();
        var (bPriv, bPub) = PairingCrypto.GenerateX25519();

        Assert.Equal(
            PairingCrypto.X25519SharedSecret(aPriv, bPub),
            PairingCrypto.X25519SharedSecret(bPriv, aPub));
    }

    [Fact]
    public void Ed25519SignAndVerifyRoundTrips()
    {
        var (seed, pub) = PairingCrypto.GenerateEd25519();
        var message = "airplay"u8.ToArray();
        var signature = PairingCrypto.Ed25519Sign(seed, message);

        Assert.True(PairingCrypto.Ed25519Verify(pub, message, signature));
        Assert.Equal(pub, PairingCrypto.Ed25519PublicFromSeed(seed));

        message[0] ^= 0xFF;
        Assert.False(PairingCrypto.Ed25519Verify(pub, message, signature));
    }

    [Fact]
    public void ChaCha20Poly1305RoundTripsWithAad()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)0x42);
        var nonce = PairingCrypto.Nonce("PS-Msg05");
        var aad = "header"u8.ToArray();
        var plaintext = "secret payload"u8.ToArray();

        var ct = PairingCrypto.ChaCha20Poly1305Encrypt(key, nonce, plaintext, aad);
        Assert.Equal(plaintext.Length + 16, ct.Length);

        var pt = PairingCrypto.ChaCha20Poly1305Decrypt(key, nonce, ct, aad);
        Assert.Equal(plaintext, pt);
    }

    [Fact]
    public void ChaCha20Poly1305TamperedTagThrows()
    {
        var key = new byte[32];
        var nonce = PairingCrypto.CounterNonce(0);
        var ct = PairingCrypto.ChaCha20Poly1305Encrypt(key, nonce, "hi"u8.ToArray());
        ct[^1] ^= 0x01;

        Assert.ThrowsAny<Exception>(() => PairingCrypto.ChaCha20Poly1305Decrypt(key, nonce, ct));
    }

    [Fact]
    public void CounterNonceIsLittleEndianInLowBytes()
    {
        var nonce = PairingCrypto.CounterNonce(1);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, nonce);
    }
}
