namespace Streamsemble.AirPlay.Receiver;

public sealed class AirPlayReceiverOptions
{
    public bool Enabled { get; set; }

    /// <summary>Advertised speaker name; null falls back to the global device name.</summary>
    public string? Name { get; set; }

    /// <summary>RTSP listening port (5000 = classic AirPlay, 7000 = AirPlay 2).</summary>
    public int Port { get; set; } = 7000;
}
