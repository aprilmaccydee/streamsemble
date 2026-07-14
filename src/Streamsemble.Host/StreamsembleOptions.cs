namespace Streamsemble.Host;

public sealed class StreamsembleOptions
{
    /// <summary>Name this hub advertises as a Spotify Connect / AirPlay device.</summary>
    public string DeviceName { get; set; } = "Streamsemble";

    /// <summary>Which sink receives the live source: Wav | Null | AirPlay.</summary>
    public string Sink { get; set; } = "Wav";

    /// <summary>Recording directory for the Wav sink.</summary>
    public string WavDirectory { get; set; } = "recordings";
}
