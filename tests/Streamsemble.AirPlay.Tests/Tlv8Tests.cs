using Streamsemble.AirPlay.Common.Hap;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class Tlv8Tests
{
    [Fact]
    public void RoundTripsSimpleItems()
    {
        var encoded = new Tlv8()
            .AddState(1)
            .Add(TlvType.Method, (byte)PairingMethod.PairSetup)
            .Add(TlvType.Identifier, "hello"u8.ToArray())
            .Encode();

        var decoded = Tlv8.Decode(encoded);
        Assert.Equal((byte)1, decoded.GetByte(TlvType.State));
        Assert.Equal((byte)PairingMethod.PairSetup, decoded.GetByte(TlvType.Method));
        Assert.Equal("hello"u8.ToArray(), decoded.Get(TlvType.Identifier));
    }

    [Fact]
    public void FragmentsValuesOver255Bytes()
    {
        var big = new byte[600];
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = (byte)(i % 251);
        }

        var encoded = new Tlv8().Add(TlvType.PublicKey, big).Encode();

        // 600 bytes → 255 + 255 + 90, three items each with a 2-byte header.
        Assert.Equal(600 + 3 * 2, encoded.Length);
        Assert.Equal(255, encoded[1]);

        var decoded = Tlv8.Decode(encoded);
        Assert.Equal(big, decoded.Get(TlvType.PublicKey));
    }

    [Fact]
    public void Exactly255ByteValueReassembles()
    {
        var value = new byte[255];
        Array.Fill(value, (byte)0xAB);
        var encoded = new Tlv8().Add(TlvType.Salt, value).Encode();

        Assert.Equal(value, Tlv8.Decode(encoded).Get(TlvType.Salt));
    }

    [Fact]
    public void MissingTypeReadsAsNull()
    {
        var decoded = Tlv8.Decode(new Tlv8().AddState(6).Encode());
        Assert.Null(decoded.Get(TlvType.Signature));
        Assert.Null(decoded.GetByte(TlvType.Error));
    }
}
