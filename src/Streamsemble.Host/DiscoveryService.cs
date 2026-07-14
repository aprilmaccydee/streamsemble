using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Streamsemble.Discovery;

namespace Streamsemble.Host;

/// <summary>
/// Continuously browses <c>_raop._tcp</c> and feeds the discovered-speaker
/// store the web UI reads. One rolling scan; entries age out in the store.
/// </summary>
public sealed class DiscoveryService(
    AirPlayBrowser browser,
    DiscoveredTargetStore store,
    ILogger<DiscoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var found = await browser.BrowseAsync(TimeSpan.FromSeconds(4), stoppingToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                foreach (var target in found)
                {
                    var protocol = target.SupportsAirPlay2 ? "AirPlay2" : "Raop";
                    store.Seen(target.DisplayName, target.Address.ToString(), target.RaopPort, protocol, now);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "mDNS discovery scan failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
