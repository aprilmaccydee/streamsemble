namespace Streamsemble.AirPlay.Receiver;

/// <summary>Identity constants the receiver reports consistently across mDNS, /info, and RTSP replies.</summary>
public static class ReceiverConstants
{
    /// <summary>
    /// AirPlay source version we emulate; must match the advertised srcvers/vs
    /// TXT records. Kept ≥ 370 deliberately: senders (real Macs and our own
    /// AirPlay2Session) choose the legacy u32 buffered framing — whose AAD was
    /// never wire-verified — for receivers reporting &lt; 370, and the modern
    /// u16/0x80-seq framing (AAD pcap-confirmed) for newer ones.
    /// </summary>
    public const string SourceVersion = "377.40.00";

    /// <summary>RTSP Server header value.</summary>
    public const string ServerAgent = $"AirTunes/{SourceVersion}";

    /// <summary>Model string, also in mDNS TXT.</summary>
    public const string Model = "Streamsemble1,1";
}
