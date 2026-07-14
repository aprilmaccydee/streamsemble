using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Zeroconf;

namespace Streamsemble.Discovery;

public sealed record ResolvedTarget(
    string DisplayName,
    IPAddress Address,
    int RaopPort,
    IReadOnlyDictionary<string, string> RaopTxt,
    int? AirPlayPort = null,
    ulong Features = 0,
    bool HasRaop = true)
{
    /// <summary>
    /// Whether the speaker speaks the AirPlay 2 protocol our sender implements:
    /// bit 38 = buffered audio (the canonical "is AirPlay 2" flag), bit 48 =
    /// transient HomeKit pairing (what AirPlay2Session actually performs).
    /// A device with no _raop record at all (TVs) can only be AirPlay 2.
    /// </summary>
    public bool SupportsAirPlay2 => !HasRaop || (Features & (1UL << 38 | 1UL << 48)) != 0;
}

/// <summary>
/// Browses <c>_raop._tcp</c> + <c>_airplay._tcp</c> and resolves a configured
/// speaker name (the part after the <c>MAC@</c> prefix in the RAOP instance
/// name, matched case-insensitively as a substring) to an address, ports, TXT
/// record and AirPlay feature bits.
/// </summary>
public sealed class AirPlayBrowser(ILogger<AirPlayBrowser> logger)
{
    private const string RaopService = "_raop._tcp.local.";
    private const string AirPlayService = "_airplay._tcp.local.";

    public async Task<IReadOnlyList<ResolvedTarget>> BrowseAsync(TimeSpan scanTime, CancellationToken ct = default)
    {
        var hosts = await ZeroconfResolver.ResolveAsync(new[] { RaopService, AirPlayService }, scanTime, cancellationToken: ct).ConfigureAwait(false);
        var targets = new List<ResolvedTarget>();
        foreach (var host in hosts)
        {
            if (!IPAddress.TryParse(host.IPAddress, out var address))
            {
                continue;
            }

            // The _airplay record (if any) carries the authoritative feature
            // bits and the AirPlay 2 RTSP port; the _raop record carries the
            // classic name/port/TXT. Devices advertise both from one host.
            var airplay = host.Services.Values.FirstOrDefault(s => IsService(s, "_airplay._tcp"));
            var airplayTxt = airplay is null ? null : TxtOf(airplay);
            var hasRaop = false;

            foreach (var service in host.Services.Values)
            {
                if (!IsService(service, "_raop._tcp"))
                {
                    continue;
                }

                hasRaop = true;
                var txt = TxtOf(service);
                var features = ParseFeatures(
                    airplayTxt is not null && airplayTxt.TryGetValue("features", out var f) ? f
                    : txt.TryGetValue("ft", out var ft) ? ft
                    : null);

                var displayName = InstanceName(service.ServiceName, "._raop._tcp");
                displayName = displayName.Contains('@') ? displayName[(displayName.IndexOf('@') + 1)..] : displayName;
                targets.Add(new ResolvedTarget(displayName, address, service.Port, txt, airplay?.Port, features));
            }

            // TVs and other AirPlay-2-only receivers advertise _airplay._tcp
            // without any _raop record — iOS/macOS discover them via _airplay,
            // so we must too.
            if (!hasRaop && airplay is not null)
            {
                var features = ParseFeatures(airplayTxt!.TryGetValue("features", out var f) ? f : null);
                targets.Add(new ResolvedTarget(
                    InstanceName(airplay.ServiceName, "._airplay._tcp"),
                    address, airplay.Port, airplayTxt, airplay.Port, features, HasRaop: false));
            }
        }

        return targets;
    }

    private static string InstanceName(string serviceName, string typeSuffix)
    {
        var typeStart = serviceName.IndexOf(typeSuffix, StringComparison.OrdinalIgnoreCase);
        return typeStart > 0 ? serviceName[..typeStart] : serviceName;
    }

    private static bool IsService(IService service, string type)
        => service.Name.Contains(type, StringComparison.OrdinalIgnoreCase)
           || service.ServiceName.Contains(type, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> TxtOf(IService service) => service.Properties
        .SelectMany(set => set)
        .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses the AirPlay feature bitmask TXT value: either a single hex number
    /// or the split "0xLOW32,0xHIGH32" form used by modern firmware.
    /// </summary>
    public static ulong ParseFeatures(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        var low = ParseHex(parts[0]);
        var high = parts.Length > 1 ? ParseHex(parts[1]) : 0;
        return high << 32 | low;
    }

    private static ulong ParseHex(string text)
    {
        var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    public async Task<ResolvedTarget?> ResolveByNameAsync(string name, TimeSpan scanTime, CancellationToken ct = default)
    {
        var targets = await BrowseAsync(scanTime, ct).ConfigureAwait(false);
        var match = targets.FirstOrDefault(t => t.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            logger.LogWarning(
                "AirPlay target \"{Name}\" not found; discovered: {Discovered}",
                name,
                string.Join(", ", targets.Select(t => t.DisplayName).DefaultIfEmpty("(none)")));
        }

        return match;
    }
}
