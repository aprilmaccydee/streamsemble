namespace Streamsemble.AirPlay.Receiver;

public sealed class AirPlayReceiverOptions
{
    public bool Enabled { get; set; }

    /// <summary>Advertised speaker name; null falls back to the global device name.</summary>
    public string? Name { get; set; }

    /// <summary>RTSP listening port (5000 = classic AirPlay, 7000 = AirPlay 2).</summary>
    public int Port { get; set; } = 7000;

    /// <summary>
    /// The hub's render lead, in samples at 44.1 kHz: how long audio emitted
    /// into the pipeline takes to turn audible on the speaker group (the
    /// group presentation latency; Host wires it from the sender side).
    /// Inbound anchored render times are honored by emitting this much
    /// EARLY, so audio is audible AT the sender's stated time and the
    /// receiver truthfully declares zero latency — the TV contract. NOT
    /// reported to senders: the 2026-07-26 experiments proved declared
    /// latency isn't honored consistently (ignored for realtime video,
    /// double-counted across surfaces). Must stay below the realtime
    /// sender's ~1.75 s transmission lead or audio trails by the difference.
    /// </summary>
    public int PresentationLatencySamples { get; set; }
}
