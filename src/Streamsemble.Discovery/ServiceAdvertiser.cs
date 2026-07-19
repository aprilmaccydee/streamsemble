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
        // Routable IPv4 only — deliberately NOT the library default of every
        // NIC address. The default publishes an AAAA with the link-local v6,
        // macOS resolves that first and runs the whole AirPlay session against
        // it; TCP survives, but the sender's audio UDP channel (NW connection
        // to our advertised dataPort) silently refuses to dial a bare fe80 —
        // engine runs, zero packets sent, no error anywhere. Every receiver a
        // Mac demonstrably streams to resolves to a routable v4.
        var addresses = MulticastService.GetIPAddresses()
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(a)
                && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .ToArray();
        var profile = addresses.Length > 0
            ? new ServiceProfile(instanceName, serviceType, (ushort)port, addresses)
            : new ServiceProfile(instanceName, serviceType, (ushort)port);
        // Only the TXT strings are replaced: the default txtvers=1 entry
        // confuses AirPlay senders, which expect their own keys first.
        var txtRecord = profile.Resources.OfType<TXTRecord>().First();
        txtRecord.Strings.Clear();
        foreach (var (key, value) in txt)
        {
            txtRecord.Strings.Add($"{key}={value}");
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
