namespace Streamsemble.AirPlay.Sender;

public sealed class AirPlaySenderOptions
{
    public List<AirPlayTargetOptions> Targets { get; set; } = [];

    /// <summary>UDP port for the shared NTP timing responder; 0 picks a free port.</summary>
    public int TimingPort { get; set; }

    /// <summary>UDP port for the shared control channel (sync out, retransmit in); 0 picks a free port.</summary>
    public int ControlPort { get; set; }

    /// <summary>mDNS scan time when resolving targets by name.</summary>
    public double ScanSeconds { get; set; } = 4;
}

public sealed class AirPlayTargetOptions
{
    /// <summary>Speaker name to resolve via mDNS (substring match on the advertised name).</summary>
    public string? Name { get; set; }

    /// <summary>Direct address, bypassing discovery. One of Name/Host is required.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 5000;

    /// <summary>Raop | AirPlay2 | Auto. AirPlay2 (HAP-paired, for HomePods) lands in M4.</summary>
    public string Protocol { get; set; } = "Auto";

    /// <summary>None | Rsa | Auto (Auto reads the device TXT record's et list).</summary>
    public string Encryption { get; set; } = "Auto";

    /// <summary>
    /// AirPlay 2 stream mode: Auto | Realtime | Buffered. Auto asks the device
    /// (/info audioLatencies) — TVs render only buffered (AAC/TCP) streams,
    /// speakers like Sonos use realtime (ALAC/UDP).
    /// </summary>
    public string StreamMode { get; set; } = "Auto";

    /// <summary>Manual output-alignment trim for this device, positive = play later.</summary>
    public int LatencyTrimMs { get; set; }
}
