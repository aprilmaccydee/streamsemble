using System.Collections.Concurrent;

namespace Streamsemble.Discovery;

/// <summary>A speaker seen on the network, with the last time we saw it.</summary>
public sealed record DiscoveredSpeaker(string Name, string Host, int Port, string Protocol, DateTimeOffset LastSeen)
{
    /// <summary>Stable id for the UI — the advertised name (unique per speaker on a LAN).</summary>
    public string Id => Name;
}

/// <summary>
/// Thread-safe snapshot of AirPlay speakers currently visible via mDNS,
/// refreshed by <c>DiscoveryService</c> and read by the web API. Entries that
/// stop being advertised age out.
/// </summary>
public sealed class DiscoveredTargetStore
{
    private readonly ConcurrentDictionary<string, DiscoveredSpeaker> _speakers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(30);

    public void Seen(string name, string host, int port, string protocol, DateTimeOffset now)
    {
        _speakers[name] = new DiscoveredSpeaker(name, host, port, protocol, now);
    }

    public IReadOnlyList<DiscoveredSpeaker> Current(DateTimeOffset now)
    {
        foreach (var (key, speaker) in _speakers)
        {
            if (now - speaker.LastSeen > _staleAfter)
            {
                _speakers.TryRemove(key, out _);
            }
        }

        return _speakers.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
