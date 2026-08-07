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

    /// <summary>Base color for the Pulse mode, "#RRGGBB".</summary>
    public string Color { get; set; } = "#FF4000";

    /// <summary>Master brightness scale for rendered light frames, 0–1.</summary>
    public double Brightness { get; set; } = 1.0;

    /// <summary>Seconds the device holds realtime mode after the last packet before
    /// returning to its own effect (255 = hold until reboot).</summary>
    public int TimeoutSeconds { get; set; } = 2;
}
