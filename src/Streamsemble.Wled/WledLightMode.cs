namespace Streamsemble.Wled;

/// <summary>How a strip visualizes the music.</summary>
public enum WledLightMode
{
    /// <summary>The lighting engine leaves the strip alone (manual /api/wled sends only).</summary>
    Off,

    /// <summary>The whole strip glows the device's color, brightness following loudness.</summary>
    Pulse,

    /// <summary>Classic meter: LEDs fill with loudness, green → amber → red along the strip.</summary>
    Vu,

    /// <summary>Frequency bands across the strip, bass at the start, hue by pitch.</summary>
    Spectrum,
}

public static class WledLightModes
{
    public static WledLightMode Parse(string mode) => mode.ToLowerInvariant() switch
    {
        "off" => WledLightMode.Off,
        "pulse" => WledLightMode.Pulse,
        "vu" => WledLightMode.Vu,
        "spectrum" => WledLightMode.Spectrum,
        var other => throw new InvalidOperationException(
            $"Unknown WLED light mode \"{other}\" (expected Off, Pulse, Vu or Spectrum)"),
    };
}
