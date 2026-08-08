using System.Buffers.Binary;
using System.Security.Cryptography;
using Streamsemble.AirPlay.Common.FairPlay;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

/// <summary>
/// Known-answer tests for the FairPlay key unwrap. There is no partial credit
/// with a key derivation — it is either byte-exact or it produces noise — so
/// every primitive is pinned separately, and then the whole unwrap is pinned
/// against a captured handshake whose expected output comes from an independent
/// implementation of the same protocol.
/// </summary>
public class FairPlayKeyTests
{
    /// <summary>A repeatable 64-byte block; the primitives take exactly one.</summary>
    private static byte[] PatternBlock()
    {
        var block = new byte[64];
        for (var i = 0; i < block.Length; i++)
        {
            block[i] = (byte)(i * 3 + 1);
        }

        return block;
    }

    [Fact]
    public void ModifiedMd5CompressionMatchesReferenceVector()
    {
        var key = new byte[16];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)i;
        }

        var words = FairPlayMd5.Compress(FairPlayMd5.WordsFromLittleEndian(key), PatternBlock(), FairPlayMd5Mutation.Kdf);

        var digest = new byte[16];
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(i * 4), words[i]);
        }

        Assert.Equal("F6F728CB5A4397B675664F9291B859AA", Convert.ToHexString(digest));
    }

    [Fact]
    public void SapHashMatchesReferenceVector()
    {
        Assert.Equal("75498A4E218773030E9CDF04F0C49367", Convert.ToHexString(FairPlaySapHash.Compute(PatternBlock())));
    }

    /// <summary>
    /// One vector only proves one input. This walks 64 pseudo-random blocks and
    /// checks the aggregate, so a branch that only fires on particular byte
    /// values — of which this function has many — cannot hide.
    /// </summary>
    [Fact]
    public void SapHashMatchesReferenceCorpus()
    {
        var aggregate = new List<byte>();
        var state = 0x6A09E667F3BCC909UL;
        for (var round = 0; round < 64; round++)
        {
            var block = new byte[64];
            for (var i = 0; i < block.Length; i++)
            {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                block[i] = (byte)state;
            }

            aggregate.AddRange(FairPlaySapHash.Compute(block));
        }

        Assert.Equal(
            "36AD2A7920076AF59452D9F0C91E3B7D1AEBC53F9143BD6819E39119D4535C92",
            Convert.ToHexString(SHA256.HashData(aggregate.ToArray())));
    }

    /// <summary>
    /// The message cipher for all four modes, each way. The round trip matters
    /// as much as the vector: the receiver decrypts a sender's m3 with it and
    /// decrypts its own m2 reply body with it too.
    /// </summary>
    [Theory]
    [InlineData(0, "B66A3295FFA6B56E02ED1B3D67FEF74B90FE148570DE65E6773669126A4905D8405644CAE0B2F5ED6109C099C7AEA7398DAC8D623FBD69B87242B374D98F89502BB5A63E29C46A8ED0E98466966191EC1E6C8675087FDE21337DB1C8FAB4C21DB824026335F6FC37E2E5B6F53357D06994BD383D6029A0AFF654FB1521BCDDE4")]
    [InlineData(1, "0F95C6DDC8987EDA18577DA2DB074E7C04715AF8B3914A73BE1B3D6C111953017EE0A39DFCAB3E0D57F2F9FBD59C5E18101788C2AB8E3CBB403BCB48B53F3E5BF74F949E79FA5CA679DF4BFCB33A69B1442675D03F948FE5BD0C5FFB64B73A5AB58F46D6BAAE097B599624147C2487991163ECFFC4D966240F9526346A10FDB0")]
    [InlineData(2, "40F18751B44D733E0AA0416401A7D3F40375FAD3CE56900602578BCA14660909820E6EF3A5E943CAFEF5370F72C52177D9B82278B414811201A3D99202BEDCCA26A4D1AD08BC2669F4BAE6CA54B8A120D0425EDB6082F51F5AECDB547BFDB319099C9EA2729AE6A1C4480827CE9991E273843CF1C7D74EBBEBC2657659BCEA9F")]
    [InlineData(3, "70A3C30EDF0E1DFA1785CE4336ED547062672A47F714A0C1F89A83D95691103DFE5CF653D4CB8299793FAF33FD0D4482EF5333B41AB094A90E1BAF996BCF4989783F6918397FBACDDAF00A2B97556DD8099841578BC5EB1444912B47298EAF356FDD6701BB3F64E725A80EB4C6F3556195DE35C93E7CC703BDD24351468E9847")]
    public void MessageCipherRoundTripsEveryMode(byte mode, string expectedPlaintext)
    {
        var message = new byte[164];
        message[12] = mode;
        for (var i = 16; i < 144; i++)
        {
            message[i] = (byte)(i * 5 + 7);
        }

        var plaintext = new byte[128];
        FairPlayMessageCipher.DecryptMessage(message, plaintext);
        Assert.Equal(expectedPlaintext, Convert.ToHexString(plaintext));

        var reencrypted = new byte[128];
        FairPlayMessageCipher.EncryptMessage(mode, plaintext, reencrypted);
        Assert.Equal(Convert.ToHexString(message.AsSpan(16, 128)), Convert.ToHexString(reencrypted));
    }

    /// <summary>
    /// Mode 3 walks its CBC chain backwards, which is what lets a caller
    /// decrypt a record over itself. Nothing in the receiver does that today,
    /// but the property is the reason the loop is written the way it is.
    /// </summary>
    [Fact]
    public void ModeThreeDecryptsInPlace()
    {
        var message = new byte[164];
        message[12] = 3;
        for (var i = 16; i < 144; i++)
        {
            message[i] = (byte)(i * 5 + 7);
        }

        var separate = new byte[128];
        FairPlayMessageCipher.DecryptMessage(message, separate);

        FairPlayMessageCipher.DecryptMessage(message, message.AsSpan(16, 128));
        Assert.Equal(Convert.ToHexString(separate), Convert.ToHexString(message.AsSpan(16, 128)));
    }

    [Theory]
    [InlineData(0, "F7DD1CCB9E745F7951A6E325D73A1F5F")]
    [InlineData(1, "B44AD891396F097AA309BC132F5B8889")]
    [InlineData(2, "D38CD8EFECDB20F333273C4312D9B236")]
    [InlineData(3, "769E2FE4C5AD7FBE6FD6772D00F529F4")]
    public void WrappingKeyMatchesReferenceVector(byte mode, string expectedKey)
    {
        var message = new byte[164];
        message[12] = mode;
        for (var i = 16; i < 144; i++)
        {
            message[i] = (byte)(i * 5 + 7);
        }

        Assert.Equal(expectedKey, Convert.ToHexString(FairPlayKeyDecryptor.DeriveWrappingKey(ReferenceReceiverSap, message)));
    }

    [Fact]
    public void UnwrapsSyntheticKeyRecord()
    {
        var m3 = new byte[164];
        m3[12] = 3;
        for (var i = 16; i < 144; i++)
        {
            m3[i] = (byte)(i * 5 + 7);
        }

        var ekey = new byte[72];
        for (var i = 0; i < ekey.Length; i++)
        {
            ekey[i] = (byte)(i * 7 + 3);
        }

        Assert.Equal("903E5BE94732428E9965AFB262B193A4", Convert.ToHexString(FairPlayKeyDecryptor.Unwrap(ReferenceReceiverSap, m3, ekey)));
    }

    /// <summary>
    /// The whole path against a real captured handshake — the test that says
    /// this works on the wire rather than merely reproducing arithmetic.
    /// </summary>
    [Fact]
    public void UnwrapsCapturedHandshake()
    {
        var m3 = Convert.FromHexString(
            "46504C590301030000000098018F1A9C7D0AF257B31F21F5C2D2BC814C032D457835AD0B06250574BBC7AB4A58C"
            + "CA6EEAD2C911D7F3E1E7ED4C058955DFF3D5CEEF014387A985BDB34995015E3DFBDACC56047CB926E093B13E9"
            + "FDB5E1EEE317C018BBC87FC5453C7671647DA686DA3D564875D03F8AEA9D60092DE06110BC7BE0C16F391C369"
            + "C75344AE47F33ACFCF10E63A9B58BFCE215E96001C49E4BE967C5067F2A");
        var ekey = Convert.FromHexString(
            "46504C59010201000000003C0000000088E4F82C8178C18B4751AC24B27C0C2A00000010C899DC6965C1081DE6"
            + "A9D966E2BA3E34548CDBC651C322DB18DC22F58FE154A60AECEE18");

        Assert.Equal(16u, BinaryPrimitives.ReadUInt32BigEndian(ekey.AsSpan(32)));
        Assert.Equal(
            "8E1214398D46D72E7B1B8E32F80C8BF0",
            Convert.ToHexString(FairPlayKeyDecryptor.Unwrap(ReferenceReceiverSap, m3, ekey)));
    }

    /// <summary>
    /// Our own m2 replies must decrypt to a well-formed SAP. This is the value
    /// a real sender wraps its key against, and unlike the vectors above it is
    /// entirely ours — if the canned reply tables were ever edited, the whole
    /// FairPlay path would break silently and this is what would catch it.
    /// </summary>
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void ReceiverSapIsRecoverableFromOurOwnReply(byte mode)
    {
        var sap = FairPlayKeyDecryptor.ReceiverSapForMode(mode);

        Assert.Equal(128, sap.Length);
        // The SAP's first two bytes are a fixed 00 01 marker in every
        // implementation of the exchange; the remaining 126 are opaque.
        Assert.Equal(0, sap[0]);
        Assert.Equal(1, sap[1]);
        Assert.Contains(sap[2..], b => b != 0);
    }

    /// <summary>A receiver SAP from an independent reference vector, used only by tests.</summary>
    private static readonly byte[] ReferenceReceiverSap = Convert.FromHexString(
        "0001CC342A5E5B1A6773C20E21B8224DF862481864EF810AAE2E3703C8819C23"
        + "539DE5F5D749BC5B7A266C496283CE7F03937AE1F616DE0C15FF338CCAFFB09E"
        + "AABBE40F5D5F558FB97F1731F8F7DA60A0EC6579C33EA98312C3B67135A6694F"
        + "F82305D9BA5C615FA254D2B1834583CEE42D4426C835A7A5F6C8421C0DA3F1C7");
}
