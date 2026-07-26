namespace Streamsemble.AirPlay.Receiver;

public sealed class AirPlayReceiverOptions
{
    public bool Enabled { get; set; }

    /// <summary>Advertised speaker name; null falls back to the global device name.</summary>
    public string? Name { get; set; }

    /// <summary>RTSP listening port (5000 = classic AirPlay, 7000 = AirPlay 2).</summary>
    public int Port { get; set; } = 7000;

    /// <summary>
    /// True presentation latency of audio streamed into this receiver, in
    /// samples at 44.1 kHz. The receiver is not the renderer — inbound audio
    /// re-anchors onto the speaker group and becomes audible one group
    /// presentation latency later — and senders only delay their local
    /// video/audio to match if we report that (RECORD Audio-Latency, /info
    /// audioLatencies). The Host wires it from the sender group's configured
    /// latency; 0 (e.g. receiver used standalone) reports an instant renderer.
    /// </summary>
    public int PresentationLatencySamples { get; set; }
}
