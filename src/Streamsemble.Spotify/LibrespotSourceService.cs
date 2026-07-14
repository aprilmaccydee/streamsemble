using Microsoft.Extensions.Hosting;

namespace Streamsemble.Spotify;

/// <summary>Hosted-service shell that runs the librespot supervision loop.</summary>
public sealed class LibrespotSourceService(LibrespotSource source) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => source.RunAsync(stoppingToken);
}
