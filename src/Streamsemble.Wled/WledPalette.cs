namespace Streamsemble.Wled;

/// <summary>How a light mode colors its pixels.</summary>
public enum WledPalette
{
    /// <summary>Each mode's traditional look: Vu green→amber→red, Spectrum
    /// bass-red→violet-treble, Pulse the device color.</summary>
    Classic,

    /// <summary>Everything in the device's color; only intensity carries the music.</summary>
    Solid,

    /// <summary>A slowly drifting full rainbow along the strip; intensity
    /// carries the music. On Pulse the whole strip cycles hue together.</summary>
    Rainbow,
}

public static class WledPalettes
{
    public static WledPalette Parse(string palette) => palette.ToLowerInvariant() switch
    {
        "classic" => WledPalette.Classic,
        "solid" => WledPalette.Solid,
        "rainbow" => WledPalette.Rainbow,
        var other => throw new InvalidOperationException(
            $"Unknown WLED palette \"{other}\" (expected Classic, Solid or Rainbow)"),
    };
}
