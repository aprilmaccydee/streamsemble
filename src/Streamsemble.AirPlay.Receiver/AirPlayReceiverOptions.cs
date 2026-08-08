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

    /// <summary>
    /// Advertise screen mirroring, so a Mac offers the hub as a mirror target
    /// and can open a type-110 video stream. Off by default because the
    /// features mask it changes is the same one the working audio negotiation
    /// depends on — see <see cref="ReceiverFeatures.ScreenMirroringAdvertised"/>.
    /// </summary>
    public bool ScreenMirroring { get; set; }

    /// <summary>
    /// The screen we tell senders we have. A Mac sizes and paces its encoder
    /// from this and will not open a video stream without it, so these are not
    /// cosmetic — set them to the display the picture ends up on.
    /// </summary>
    public int ScreenWidth { get; set; } = 1920;

    public int ScreenHeight { get; set; } = 1080;

    /// <summary>Frames per second offered to the sender's encoder.</summary>
    public int ScreenFps { get; set; } = 60;
}

/// <summary>The advertised mirroring display geometry, resolved from options.</summary>
public sealed record MirrorDisplay(int Width, int Height, int Fps)
{
    public static readonly MirrorDisplay Default = new(1920, 1080, 60);
}
