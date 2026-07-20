using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Audio;
using Xunit;

namespace Streamsemble.Core.Tests;

public class ArbiterTests
{
    private sealed class FakeSource(string name) : AudioSourceBase(name)
    {
        public TaskCompletionSource Stopped { get; } = new();

        public void GoActive() => SetState(SourceState.Active);

        public void GoIdle() => SetState(SourceState.Idle);

        public void GoPaused() => SetState(SourceState.Paused);

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped.TrySetResult();
            SetState(SourceState.Idle);
            return Task.CompletedTask;
        }
    }

    private static LastWriterWinsArbiter CreateArbiter(TimeSpan? idleGrace = null)
        => new(NullLogger<LastWriterWinsArbiter>.Instance, idleGrace);

    [Fact]
    public void LastActiveSourceWins()
    {
        var arbiter = CreateArbiter();
        var spotify = new FakeSource("Spotify");
        var airplay = new FakeSource("AirPlay");
        arbiter.Register(spotify);
        arbiter.Register(airplay);

        spotify.GoActive();
        Assert.Same(spotify, arbiter.ActiveSource);

        airplay.GoActive();
        Assert.Same(airplay, arbiter.ActiveSource);
    }

    [Fact]
    public async Task PreemptedSourceIsAskedToStop()
    {
        var arbiter = CreateArbiter();
        var spotify = new FakeSource("Spotify");
        var airplay = new FakeSource("AirPlay");
        arbiter.Register(spotify);
        arbiter.Register(airplay);

        spotify.GoActive();
        airplay.GoActive();

        await spotify.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task IdleReleasesTheSlotAfterGrace()
    {
        var arbiter = CreateArbiter(TimeSpan.FromMilliseconds(50));
        var source = new FakeSource("Spotify");
        arbiter.Register(source);

        source.GoActive();
        source.GoIdle();

        // Still live inside the grace window (a transient drop must not
        // release the slot and tear the sink down).
        Assert.Same(source, arbiter.ActiveSource);

        var released = new TaskCompletionSource();
        arbiter.ActiveSourceChanged += (_, s) =>
        {
            if (s is null)
            {
                released.TrySetResult();
            }
        };
        await released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(arbiter.ActiveSource);
    }

    [Fact]
    public async Task BriefIdleDropRidesThroughTheGrace()
    {
        var arbiter = CreateArbiter(TimeSpan.FromMilliseconds(200));
        var source = new FakeSource("Spotify");
        arbiter.Register(source);

        var sawRelease = false;
        arbiter.ActiveSourceChanged += (_, s) => sawRelease |= s is null;

        source.GoActive();
        source.GoIdle();
        source.GoActive(); // reconnected within the grace window

        await Task.Delay(400);
        Assert.Same(source, arbiter.ActiveSource);
        Assert.False(sawRelease);
    }

    [Fact]
    public void PausedSourceStaysLive()
    {
        var arbiter = CreateArbiter();
        var source = new FakeSource("Spotify");
        arbiter.Register(source);

        source.GoActive();
        source.GoPaused();

        Assert.Same(source, arbiter.ActiveSource);
    }

    [Fact]
    public void ActiveSourceChangedFires()
    {
        var arbiter = CreateArbiter();
        var source = new FakeSource("Spotify");
        arbiter.Register(source);

        IAudioSource? seen = null;
        arbiter.ActiveSourceChanged += (_, s) => seen = s;
        source.GoActive();

        Assert.Same(source, seen);
    }
}
