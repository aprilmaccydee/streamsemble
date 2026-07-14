using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Streamsemble.Cast.Stub;

public sealed class CastStubService(ILogger<CastStubService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Google Cast source is a stub: CastV2 device auth requires Google-signed device certificates, "
            + "so official sender apps reject emulated receivers. See README.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
