using System.Net;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;

namespace Streamsemble.Discovery;

/// <summary>
/// Advertises this host's services over mDNS/DNS-SD (e.g. the AirPlay
/// receiver's <c>_airplay._tcp</c> / <c>_raop._tcp</c> records). librespot
/// runs its own advertiser for <c>_spotify-connect._tcp</c>, so that service
/// is not published here.
/// </summary>
public sealed class ServiceAdvertiser : IDisposable
{
    private readonly ILogger<ServiceAdvertiser> _logger;
    private readonly MulticastService _mdns;
    private readonly ServiceDiscovery _discovery;

    public ServiceAdvertiser(ILogger<ServiceAdvertiser> logger)
    {
        _logger = logger;
        _mdns = new MulticastService();
        _discovery = new ServiceDiscovery(_mdns);
    }

    public void Advertise(string serviceType, string instanceName, int port, IReadOnlyDictionary<string, string> txt)
    {
        var profile = new ServiceProfile(instanceName, serviceType, (ushort)port, [IPAddress.Loopback]);
        profile.Resources.Clear();
        foreach (var (key, value) in txt)
        {
            profile.AddProperty(key, value);
        }

        _discovery.Advertise(profile);
        _logger.LogInformation("Advertising {Instance} {Type} on port {Port}", instanceName, serviceType, port);
    }

    public void Start() => _mdns.Start();

    public void Dispose()
    {
        _discovery.Dispose();
        _mdns.Dispose();
    }
}
