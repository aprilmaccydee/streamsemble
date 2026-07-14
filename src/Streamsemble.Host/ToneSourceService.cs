using Microsoft.Extensions.Hosting;
using Streamsemble.Core.Audio;

namespace Streamsemble.Host;

public sealed class ToneSourceService(ToneSource source) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => source.RunAsync(stoppingToken);
}
