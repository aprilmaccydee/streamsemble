using Xunit;

namespace Streamsemble.Wled.Tests;

public class WledPacketTests
{
    [Fact]
    public void DnrgbCarriesHeaderAndPixels()
    {
        var packets = WledPacketBuilder.BuildPackets(WledRealtimeMode.Dnrgb, 2, 0, new byte[] { 255, 0, 0, 0, 255, 0 });

        var packet = Assert.Single(packets);
        Assert.Equal(4, packet[0]); // protocol byte
        Assert.Equal(2, packet[1]); // timeout seconds
        Assert.Equal(0, packet[2]); // start index hi
        Assert.Equal(0, packet[3]); // start index lo
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0 }, packet[4..]);
    }

    [Fact]
    public void DnrgbStartOffsetLandsInTheHeaderBigEndian()
    {
        var packets = WledPacketBuilder.BuildPackets(WledRealtimeMode.Dnrgb, 2, 300, new byte[] { 1, 2, 3 });

        var packet = Assert.Single(packets);
        Assert.Equal(0x01, packet[2]);
        Assert.Equal(0x2C, packet[3]);
    }

    [Fact]
    public void DnrgbChunksLongStripsIntoConsecutiveStartIndexedPackets()
    {
        // 1000 LEDs: 489 + 489 + 22, each chunk addressed where the last ended.
        var rgb = new byte[1000 * 3];
        rgb[489 * 3] = 42; // first byte of the second chunk

        var packets = WledPacketBuilder.BuildPackets(WledRealtimeMode.Dnrgb, 1, 0, rgb);

        Assert.Equal(3, packets.Count);
        Assert.Equal(4 + 489 * 3, packets[0].Length);
        Assert.Equal(4 + 489 * 3, packets[1].Length);
        Assert.Equal(4 + 22 * 3, packets[2].Length);
        Assert.Equal(489, (packets[1][2] << 8) | packets[1][3]);
        Assert.Equal(978, (packets[2][2] << 8) | packets[2][3]);
        Assert.Equal(42, packets[1][4]);
    }

    [Fact]
    public void DnrgbRejectsRangesBeyondSixteenBitIndexSpace()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WledPacketBuilder.BuildPackets(WledRealtimeMode.Dnrgb, 2, -1, new byte[] { 1, 2, 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WledPacketBuilder.BuildPackets(WledRealtimeMode.Dnrgb, 2, 65534, new byte[9]));
    }

    [Fact]
    public void WarlsAddressesEachLedWithAnIndexByte()
    {
        var packets = WledPacketBuilder.BuildPackets(WledRealtimeMode.Warls, 3, 10, new byte[] { 1, 2, 3, 4, 5, 6 });

        var packet = Assert.Single(packets);
        Assert.Equal(1, packet[0]);
        Assert.Equal(3, packet[1]);
        Assert.Equal(new byte[] { 10, 1, 2, 3, 11, 4, 5, 6 }, packet[2..]);
    }

    [Fact]
    public void WarlsRejectsLedsBeyondItsOneByteIndexSpace()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => WledPacketBuilder.BuildPackets(WledRealtimeMode.Warls, 2, 255, new byte[6]));

    [Fact]
    public void DrgbPacksDenseRgbFromLedZero()
    {
        var packets = WledPacketBuilder.BuildPackets(WledRealtimeMode.Drgb, 2, 0, new byte[] { 9, 8, 7 });

        var packet = Assert.Single(packets);
        Assert.Equal(2, packet[0]);
        Assert.Equal(2, packet[1]);
        Assert.Equal(new byte[] { 9, 8, 7 }, packet[2..]);
    }

    [Fact]
    public void DrgbRejectsOffsetsAndOversizedStrips()
    {
        Assert.Throws<ArgumentException>(
            () => WledPacketBuilder.BuildPackets(WledRealtimeMode.Drgb, 2, 1, new byte[3]));
        Assert.Throws<ArgumentException>(
            () => WledPacketBuilder.BuildPackets(WledRealtimeMode.Drgb, 2, 0, new byte[491 * 3]));
    }

    [Fact]
    public void DrgbwInsertsAZeroWhiteChannelPerLed()
    {
        var packets = WledPacketBuilder.BuildPackets(WledRealtimeMode.Drgbw, 2, 0, new byte[] { 10, 20, 30, 40, 50, 60 });

        var packet = Assert.Single(packets);
        Assert.Equal(3, packet[0]);
        Assert.Equal(new byte[] { 10, 20, 30, 0, 40, 50, 60, 0 }, packet[2..]);
    }

    [Fact]
    public void DrgbwRejectsMoreLedsThanOneDatagramCarries()
        => Assert.Throws<ArgumentException>(
            () => WledPacketBuilder.BuildPackets(WledRealtimeMode.Drgbw, 2, 0, new byte[368 * 3]));

    [Theory]
    [InlineData(WledRealtimeMode.Warls)]
    [InlineData(WledRealtimeMode.Drgb)]
    [InlineData(WledRealtimeMode.Drgbw)]
    [InlineData(WledRealtimeMode.Dnrgb)]
    public void EmptyDataBuildsNoPackets(WledRealtimeMode mode)
        => Assert.Empty(WledPacketBuilder.BuildPackets(mode, 2, 0, ReadOnlyMemory<byte>.Empty));

    [Fact]
    public void RejectsPartialPixels()
        => Assert.Throws<ArgumentException>(
            () => WledPacketBuilder.BuildPackets(WledRealtimeMode.Dnrgb, 2, 0, new byte[] { 1, 2, 3, 4 }));

    [Fact]
    public void ModeNamesParseCaseInsensitively()
    {
        Assert.Equal(WledRealtimeMode.Drgbw, WledRealtimeModes.Parse("drgbw"));
        Assert.Equal(WledRealtimeMode.Dnrgb, WledRealtimeModes.Parse("DNRGB"));
        Assert.Throws<InvalidOperationException>(() => WledRealtimeModes.Parse("disco"));
    }
}
