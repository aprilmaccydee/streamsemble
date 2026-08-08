using Microsoft.Extensions.Hosting;
using Streamsemble.Core.Video;

namespace Streamsemble.Host;

/// <summary>
/// Runs the video source→sink pump for the process lifetime, the way
/// <see cref="PumpService"/> does for audio. There is no arbiter here because
/// there is nothing to arbitrate — one screen mirrors at a time.
/// </summary>
public sealed class VideoPumpService(VideoPump pump) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        pump.Start(_lifetime.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime.Cancel();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await pump.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}

/// <summary>
/// Placeholder host entry used only to run wiring that has to happen after the
/// container is built. It owns nothing and does nothing at runtime.
/// </summary>
public sealed class NoopHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
