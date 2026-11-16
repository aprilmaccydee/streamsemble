using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Streamsemble.Discovery;
using Streamsemble.Spotify;

namespace Streamsemble.Host;

/// <summary>
/// Announces librespot's zeroconf login endpoint as <c>_spotify-connect._tcp</c>
/// through the hub's own mDNS responder. Only active when
/// <see cref="SpotifyOptions.ZeroconfPort"/> is set, i.e. when librespot runs
/// with <c>--zeroconf-backend external</c> because it cannot bind UDP 5353
/// beside the hub's advertiser in the same network namespace.
/// </summary>
public sealed class SpotifyConnectAdvertiser(
    ServiceAdvertiser advertiser,
    IOptions<SpotifyOptions> spotify,
    IOptions<StreamsembleOptions> global) : IHostedService
{
    /// <summary>The TXT record librespot's own responders publish.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Txt = new Dictionary<string, string>
    {
        ["VERSION"] = "1.0",
        ["CPath"] = "/",
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = spotify.Value;
        if (options.ZeroconfPort > 0)
        {
            advertiser.Advertise(
                "_spotify-connect._tcp",
                options.DeviceName ?? global.Value.DeviceName,
                options.ZeroconfPort,
                Txt);
            advertiser.Start();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
