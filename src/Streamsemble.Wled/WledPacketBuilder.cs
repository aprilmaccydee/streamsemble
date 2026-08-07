namespace Streamsemble.Wled;

/// <summary>
/// Builds WLED UDP realtime datagrams from raw RGB data. The modes differ in
/// how pixels are addressed and whether a white channel is carried, which is
/// what bounds how many LEDs each can drive.
/// </summary>
public static class WledPacketBuilder
{
    // Per-datagram LED capacities, from WLED's 1472-byte datagram cap:
    // DNRGB 4 + 489×3 = 1471, DRGB 2 + 490×3 = 1472, DRGBW 2 + 367×4 = 1470.
    // WARLS addresses with a single index byte, so its bound is the 256-LED
    // address space (2 + 256×4 = 1026 always fits one datagram).
    private const int DnrgbLedsPerPacket = 489;
    private const int DrgbMaxLeds = 490;
    private const int DrgbwMaxLeds = 367;
    private const int WarlsMaxLeds = 256;

    public static List<byte[]> BuildPackets(WledRealtimeMode mode, byte timeoutSeconds, int startLed, ReadOnlyMemory<byte> rgb)
    {
        if (rgb.Length % 3 != 0)
        {
            throw new ArgumentException("RGB data must be a whole number of 3-byte pixels", nameof(rgb));
        }

        if (rgb.Length == 0)
        {
            return [];
        }

        return mode switch
        {
            WledRealtimeMode.Dnrgb => BuildDnrgb(timeoutSeconds, startLed, rgb),
            WledRealtimeMode.Warls => [BuildWarls(timeoutSeconds, startLed, rgb.Span)],
            WledRealtimeMode.Drgb => [BuildFromZero(WledRealtimeMode.Drgb, DrgbMaxLeds, 3, timeoutSeconds, startLed, rgb.Span)],
            WledRealtimeMode.Drgbw => [BuildFromZero(WledRealtimeMode.Drgbw, DrgbwMaxLeds, 4, timeoutSeconds, startLed, rgb.Span)],
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static List<byte[]> BuildDnrgb(byte timeoutSeconds, int startLed, ReadOnlyMemory<byte> rgb)
    {
        var ledCount = rgb.Length / 3;
        if (startLed < 0 || startLed + ledCount > 0x10000)
        {
            throw new ArgumentOutOfRangeException(nameof(startLed), "LED range must fit DNRGB's 16-bit index space");
        }

        var packets = new List<byte[]>();
        for (var offset = 0; offset < ledCount; offset += DnrgbLedsPerPacket)
        {
            var leds = Math.Min(DnrgbLedsPerPacket, ledCount - offset);
            var first = startLed + offset;
            var packet = new byte[4 + leds * 3];
            packet[0] = (byte)WledRealtimeMode.Dnrgb;
            packet[1] = timeoutSeconds;
            packet[2] = (byte)(first >> 8);
            packet[3] = (byte)first;
            rgb.Slice(offset * 3, leds * 3).CopyTo(packet.AsMemory(4));
            packets.Add(packet);
        }

        return packets;
    }

    private static byte[] BuildWarls(byte timeoutSeconds, int startLed, ReadOnlySpan<byte> rgb)
    {
        var ledCount = rgb.Length / 3;
        if (startLed < 0 || startLed + ledCount > WarlsMaxLeds)
        {
            throw new ArgumentOutOfRangeException(nameof(startLed), "WARLS addresses each LED with one index byte (0–255)");
        }

        var packet = new byte[2 + ledCount * 4];
        packet[0] = (byte)WledRealtimeMode.Warls;
        packet[1] = timeoutSeconds;
        for (var i = 0; i < ledCount; i++)
        {
            packet[2 + i * 4] = (byte)(startLed + i);
            packet[3 + i * 4] = rgb[i * 3];
            packet[4 + i * 4] = rgb[i * 3 + 1];
            packet[5 + i * 4] = rgb[i * 3 + 2];
        }

        return packet;
    }

    private static byte[] BuildFromZero(WledRealtimeMode mode, int maxLeds, int bytesPerLed, byte timeoutSeconds, int startLed, ReadOnlySpan<byte> rgb)
    {
        var ledCount = rgb.Length / 3;
        if (startLed != 0)
        {
            throw new ArgumentException($"{mode} has no start index — data always begins at LED 0 (use Dnrgb to offset)");
        }

        if (ledCount > maxLeds)
        {
            throw new ArgumentException($"{mode} fits at most {maxLeds} LEDs in its single datagram (use Dnrgb for longer strips)");
        }

        var packet = new byte[2 + ledCount * bytesPerLed];
        packet[0] = (byte)mode;
        packet[1] = timeoutSeconds;
        for (var i = 0; i < ledCount; i++)
        {
            packet[2 + i * bytesPerLed] = rgb[i * 3];
            packet[3 + i * bytesPerLed] = rgb[i * 3 + 1];
            packet[4 + i * bytesPerLed] = rgb[i * 3 + 2];
            // bytesPerLed == 4 leaves DRGBW's white channel at 0: RGB passthrough.
        }

        return packet;
    }
}
