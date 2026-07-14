using Microsoft.Extensions.Hosting;
using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Audio;

namespace Streamsemble.Host;

/// <summary>
/// Registers every configured source with the arbiter and runs the
/// source→sink pump for the process lifetime.
/// </summary>
public sealed class PumpService(
    ISourceArbiter arbiter,
    IEnumerable<IAudioSource> sources,
    AudioPump pump) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            arbiter.Register(source);
        }

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
