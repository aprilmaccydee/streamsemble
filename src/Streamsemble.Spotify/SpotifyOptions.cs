namespace Streamsemble.Spotify;

public sealed class SpotifyOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Path to the librespot binary; bare name resolves via PATH.</summary>
    public string LibrespotPath { get; set; } = "librespot";

    /// <summary>Spotify Connect device name; null falls back to the global device name.</summary>
    public string? DeviceName { get; set; }

    public int Bitrate { get; set; } = 320;

    /// <summary>librespot credential/audio cache directory; defaults under the user profile.</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>Port for the local librespot --onevent callback endpoint; 0 picks a free port.</summary>
    public int EventPort { get; set; }

    public string? ExtraArgs { get; set; }

    /// <summary>
    /// 0 (default): librespot announces itself with its own mDNS responder.
    /// Non-zero: librespot serves its zeroconf login endpoint on this TCP port
    /// with <c>--zeroconf-backend external</c> and the hub announces
    /// <c>_spotify-connect._tcp</c> instead. Needed where the hub's advertiser
    /// already holds UDP 5353 and librespot cannot bind beside it; requires
    /// the librespot build from <c>tools/librespot-external-zeroconf.patch</c>.
    /// </summary>
    public int ZeroconfPort { get; set; }
}
