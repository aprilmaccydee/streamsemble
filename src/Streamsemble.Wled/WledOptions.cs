namespace Streamsemble.Wled;

public sealed class WledOptions
{
    /// <summary>The WLED devices this hub can drive; empty leaves the feature disabled.</summary>
    public List<WledDeviceOptions> Devices { get; set; } = [];
}

public sealed class WledDeviceOptions
{
    /// <summary>Name used to address the device in the API and UI; defaults to Host.</summary>
    public string? Name { get; set; }

    /// <summary>Hostname or IP of the WLED device.</summary>
    public string Host { get; set; } = "";

    /// <summary>UDP realtime port (WLED's default is 21324).</summary>
    public int Port { get; set; } = 21324;

    /// <summary>Number of LEDs on this strip; renders fill this many pixels.</summary>
    public int LedCount { get; set; } = 30;

    /// <summary>Wire protocol for the strip: Dnrgb | Drgb | Drgbw | Warls.
    /// Dnrgb suits any RGB strip; Drgbw carries a white channel for RGBW strips.</summary>
    public string Protocol { get; set; } = "Dnrgb";

    /// <summary>Initial light mode: Off | Pulse | Vu | Spectrum. Runtime-switchable
    /// via the web UI / POST /api/wled/config.</summary>
    public string Mode { get; set; } = "Spectrum";

    /// <summary>Base color, "#RRGGBB": the Pulse glow, and every mode under the
    /// Solid palette.</summary>
    public string Color { get; set; } = "#FF4000";

    /// <summary>Master brightness scale for rendered light frames, 0–1.</summary>
    public double Brightness { get; set; } = 1.0;

    /// <summary>Color treatment: Classic | Solid | Rainbow. Classic is each
    /// mode's traditional look (Vu green→red meter, Spectrum bass-red→violet);
    /// Solid recolors everything with Color; Rainbow drifts a full rainbow
    /// along the strip. Runtime-switchable.</summary>
    public string Palette { get; set; } = "Classic";

    /// <summary>Fall half-life in seconds, 0–1: how slowly the lights fade
    /// after a hit. 0 snaps with the analyzer's own envelope; 1 trails a long
    /// silky tail. Runtime-tunable.</summary>
    public double Decay { get; set; } = 0.0;

    /// <summary>Render from the strip's center outward (mirrored halves).</summary>
    public bool Mirror { get; set; }

    /// <summary>Flip the strip end-for-end — mounted backwards, or the meter
    /// origin belongs at the far end. With Mirror, the show grows from the
    /// ends inward instead.</summary>
    public bool Reverse { get; set; }

    /// <summary>Seconds the device holds realtime mode after the last packet before
    /// returning to its own effect (255 = hold until reboot).</summary>
    public int TimeoutSeconds { get; set; } = 2;
}
