namespace Streamsemble.Wled;

/// <summary>WLED UDP realtime protocol variants; each value is its wire protocol byte.</summary>
public enum WledRealtimeMode : byte
{
    /// <summary>Index-addressed RGB, one index byte per LED — strips up to 256 LEDs.</summary>
    Warls = 1,

    /// <summary>Dense RGB from LED 0 in a single datagram — up to 490 LEDs.</summary>
    Drgb = 2,

    /// <summary>Dense RGBW from LED 0 in a single datagram — up to 367 LEDs.</summary>
    Drgbw = 3,

    /// <summary>Dense RGB with a 16-bit start index — any strip length. The default.</summary>
    Dnrgb = 4,
}

public static class WledRealtimeModes
{
    public static WledRealtimeMode Parse(string mode) => mode.ToLowerInvariant() switch
    {
        "warls" => WledRealtimeMode.Warls,
        "drgb" => WledRealtimeMode.Drgb,
        "drgbw" => WledRealtimeMode.Drgbw,
        "dnrgb" => WledRealtimeMode.Dnrgb,
        var other => throw new InvalidOperationException(
            $"Unknown WLED mode \"{other}\" (expected Warls, Drgb, Drgbw or Dnrgb)"),
    };
}
