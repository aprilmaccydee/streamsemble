namespace Streamsemble.AirPlay.Receiver;

public sealed class AirPlayReceiverOptions
{
    public bool Enabled { get; set; }

    /// <summary>Advertised speaker name; null falls back to the global device name.</summary>
    public string? Name { get; set; }

    /// <summary>RTSP listening port (5000 = classic AirPlay, 7000 = AirPlay 2).</summary>
    public int Port { get; set; } = 7000;

    /// <summary>
    /// Emit-scheduling lead, in samples at 44.1 kHz: inbound frames are
    /// released to the pipeline this far before their stamped render
    /// deadline (Host wires it as group latency + slack). Sync does NOT
    /// depend on this value — every frame carries its absolute deadline and
    /// the sink derives the send timeline from the stamps — it only needs
    /// to be large enough that the data is downstream before it is due, and
    /// small enough to fit inside the realtime sender's ~1.75 s
    /// transmission lead.
    /// </summary>
    public int PresentationLatencySamples { get; set; }
}
