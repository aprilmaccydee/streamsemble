using Streamsemble.AirPlay.Common.Hap;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class PairingServerTests
{
    [Fact]
    public void HapSrpServerInteropsWithWireValidatedClient()
    {
        // HapSrpClient is byte-exact against srptools reference vectors (see
        // CryptoTests), so agreeing with it proves the server speaks the HAP
        // wire formulas — unlike Srp6.Server, which uses BouncyCastle's.
        var server = new HapSrpServer(HapConstants.TransientPin);
        var client = new HapSrpClient();

        var m1 = client.ComputeProof(server.Salt, server.PublicB);
        Assert.True(server.VerifyClientProof(client.PublicA, m1));
        Assert.Equal(client.SessionKey, server.SessionKey);
        Assert.True(client.VerifyServerProof(server.ComputeServerProof()));
    }

    [Fact]
    public void HapSrpServerRejectsWrongPin()
    {
        var server = new HapSrpServer(HapConstants.TransientPin);
        var client = new HapSrpClient();

        var m1 = client.ComputeProof(server.Salt, server.PublicB, password: "0000");
        Assert.False(server.VerifyClientProof(client.PublicA, m1));
    }

    [Fact]
    public void HapSrpServerPublicBIsFullGroupWidth()
    {
        // We regenerate b until B has no leading zero byte, so senders that
        // hash B exactly as transmitted agree with our minimal-magnitude math.
        var server = new HapSrpServer(HapConstants.TransientPin);
        Assert.Equal(384, server.PublicB.Length); // 3072-bit group
        Assert.NotEqual(0, server.PublicB[0]);
        Assert.NotEqual(0, server.Salt[0]);
    }

    [Fact]
    public void TransientPairSetupClientAndServerRoundTrip()
    {
        var client = new TransientPairSetupClient();
        var server = new TransientPairSetupServer();

        var m2 = server.HandleMessage(client.BuildM1());
        var m3 = client.HandleM2BuildM3(m2);
        var m4 = server.HandleMessage(m3);
        client.HandleM4(m4);

        Assert.NotNull(server.SharedSecret);
        Assert.Equal(client.SharedSecret, server.SharedSecret);
        Assert.Equal(64, server.SharedSecret!.Length);
    }

    [Fact]
    public void TransientPairSetupServerSignalsAuthenticationErrorOnBadProof()
    {
        var client = new TransientPairSetupClient();
        var server = new TransientPairSetupServer();

        var m2 = server.HandleMessage(client.BuildM1());
        var m3 = client.HandleM2BuildM3(m2);

        // Corrupt the proof.
        var tampered = Tlv8.Decode(m3);
        var badProof = (byte[])tampered.Get(TlvType.Proof)!.Clone();
        badProof[0] ^= 0xFF;
        var m3Bad = new Tlv8()
            .AddState(3)
            .Add(TlvType.PublicKey, tampered.Get(TlvType.PublicKey)!)
            .Add(TlvType.Proof, badProof)
            .Encode();

        var m4 = Tlv8.Decode(server.HandleMessage(m3Bad));
        Assert.Equal((byte)PairingError.Authentication, m4.GetByte(TlvType.Error));
        Assert.Null(server.SharedSecret);
    }

    [Fact]
    public void FairPlaySetupPhase1RepliesPerModeTable()
    {
        var request = new byte[16];
        "FPLY"u8.CopyTo(request);
        request[4] = 0x03;
        request[5] = 0x01;
        request[6] = 0x01;

        for (byte mode = 0; mode < 4; mode++)
        {
            request[14] = mode;
            var reply = FairPlaySetup.HandleSetupPhase1(request);
            Assert.Equal(142, reply.Length);
            Assert.Equal("FPLY"u8.ToArray(), reply[..4]);
        }
    }

    [Fact]
    public void FairPlaySetupPhase2EchoesRequestTail()
    {
        var request = new byte[164];
        "FPLY"u8.CopyTo(request);
        request[4] = 0x03;
        request[5] = 0x01;
        request[6] = 0x03;
        for (var i = 144; i < 164; i++)
        {
            request[i] = (byte)i;
        }

        var reply = FairPlaySetup.HandleSetupPhase2(request);
        Assert.Equal(32, reply.Length);
        Assert.Equal(request[144..], reply[12..]);
    }
}
