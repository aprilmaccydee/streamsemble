using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Streamsemble.AirPlay.Sender;
using Streamsemble.AirPlay.Sender.AirPlay2;
using Streamsemble.Core.Audio;
using Streamsemble.Core.Metadata;
using Streamsemble.Timing;
using Streamsemble.Timing.Ptp;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class AirPlayGroupRecoveryTests
{
    [Fact]
    public async Task CutoverDropsTheInHandOldGenerationAndForwardsTheFirstFreshFrame()
    {
        await using var fixture = new Fixture();
        fixture.Set("_holdFrames", true);
        fixture.StartSendLoop();
        await fixture.Group.WriteAsync(Frame(1, 0));
        await UntilAsync(() => fixture.QueueDepth == 0);

        await fixture.Group.FlushAsync(dropQueuedAudio: true);
        await fixture.Group.WriteAsync(Frame(2, PcmFrame.SamplesPerFrame));
        await fixture.Group.ResumeAsync();

        Assert.Equal(2, await fixture.NextRenderedAsync());
        Assert.False(fixture.Rendered.TryRead(out _));
    }

    [Fact]
    public async Task FreshDispatchWaitsForEverySpeakerFlushToFinish()
    {
        await using var fixture = new Fixture();
        var session = new FakeSession { HoldFlush = true };
        fixture.Sessions.Add("speaker", session);
        var oldGeneration = fixture.Get<long>("_queueGeneration");
        var flush = fixture.Group.FlushAsync(dropQueuedAudio: true);
        await session.FlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var freshDispatch = fixture.AcquireDispatchAsync(fixture.Get<long>("_queueGeneration"));
        try
        {
            Assert.False(freshDispatch.IsCompleted);
            Assert.False(await fixture.AcquireDispatchAsync(oldGeneration));
        }
        finally
        {
            session.ReleaseFlush.TrySetResult();
        }

        await flush.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await freshDispatch.WaitAsync(TimeSpan.FromSeconds(2)));
        fixture.Get<SemaphoreSlim>("_dispatchGate").Release();
    }

    [Fact]
    public async Task CoordinatedRecoveryClearsEveryTimelineMappingEvenIfOneSessionDisposeFails()
    {
        await using var fixture = new Fixture();
        var broken = new FakeSession { ThrowOnDispose = true };
        var other = new FakeSession();
        fixture.Sessions.Add("broken", broken);
        fixture.Sessions.Add("other", other);
        fixture.Set("_timestampBase", 44_100L);
        fixture.Set("_sourceEpochNanos", 123L);
        fixture.Set("_stampShiftNanos", 5_000_000_000L);
        fixture.Set("_holdFrames", true);
        var targetBaseType = typeof(AirPlayTargetGroup).GetNestedType("TargetBase", BindingFlags.NonPublic)!;
        var targetBase = Activator.CreateInstance(targetBaseType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [44_100L, 1_000_000_000L], culture: null);
        fixture.Set("_sourceTargetBase", targetBase);
        var epoch = fixture.Get<GroupTimelineAnchor>("_groupAnchor");
        epoch.Map(44_100, 6_000_000_000);
        fixture.Get<Dictionary<string, DateTimeOffset>>("_retryAt").Add("broken", DateTimeOffset.MaxValue);
        var generation = fixture.Get<long>("_queueGeneration");
        var originalEpoch = epoch.Snapshot();

        fixture.Invoke("RequestTimelineRecovery", "expired anchor");
        fixture.Invoke("RequestTimelineRecovery", "another expired anchor");
        Assert.Equal(1, fixture.Get<int>("_timelineRecoveryRequested"));
        Assert.Equal(originalEpoch, epoch.Snapshot());
        Assert.Equal(generation, fixture.Get<long>("_queueGeneration"));
        Assert.False(other.Disposed);

        await ((Task)fixture.Invoke("RecoverGroupTimelineAsync", CancellationToken.None)!).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(broken.Disposed);
        Assert.True(other.Disposed);
        Assert.Empty(fixture.Sessions);
        Assert.Empty(fixture.Get<Dictionary<string, DateTimeOffset>>("_retryAt"));
        Assert.Null(fixture.Get<object?>("_timestampBase"));
        Assert.Null(fixture.Get<object?>("_sourceTargetBase"));
        Assert.Equal(long.MaxValue, fixture.Get<long>("_sourceEpochNanos"));
        Assert.Equal(0, fixture.Group.StampShiftNanos);
        var replacementEpoch = fixture.Get<GroupTimelineAnchor>("_groupAnchor");
        Assert.NotSame(epoch, replacementEpoch);
        Assert.Null(replacementEpoch.Snapshot());
        // A retired pump may still finish its old anchor calculation after
        // Dispose cancels it; it must not populate the replacement mapping.
        epoch.Reset();
        epoch.Map(44_100, 9_000_000_000);
        Assert.Null(replacementEpoch.Snapshot());
        Assert.Null(fixture.Group.SecondsUntilAudible(44_100));
        Assert.NotEqual(generation, fixture.Get<long>("_queueGeneration"));
        Assert.True(fixture.Get<bool>("_holdFrames"));
        Assert.NotNull(fixture.Get<CancellationTokenSource?>("_streamCts"));
    }

    [Fact]
    public async Task PauseInterruptsAFarFutureDeadlineWithoutDiscardingItsFrame()
    {
        await using var fixture = new Fixture();
        fixture.StartSendLoop();
        await fixture.Group.WriteAsync(Frame(7, 0, PtpReceiverClock.NowNanos + 60_000_000_000L));
        await UntilAsync(() => fixture.Get<object?>("_timestampBase") is not null);
        var generation = fixture.Get<long>("_queueGeneration");

        await fixture.Group.FlushAsync(dropQueuedAudio: false).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Get<bool>("_holdFrames"));
        Assert.Equal(generation, fixture.Get<long>("_queueGeneration"));
        Assert.False(fixture.Rendered.TryRead(out _));

        await fixture.Group.ResumeAsync();
        await UntilAsync(() => fixture.Get<object?>("_sourceTargetBase") is not null);
        fixture.Clock.Advance(120);
        Assert.Equal(7, await fixture.NextRenderedAsync());
    }

    private static PcmFrame Frame(byte id, long timestamp, long target = 0)
    {
        var pcm = new byte[PcmFrame.CanonicalFrameBytes];
        Array.Fill(pcm, id);
        return new PcmFrame(pcm, timestamp, target);
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }

    // Build the production dispatcher without opening sockets or starting
    // discovery. Reflection only supplies private queue/state boundaries;
    // WriteAsync, SendLoopAsync, FlushAsync and recovery run unchanged.
    private sealed class Fixture : IAsyncDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly object _reader;
        private readonly Channel<byte> _rendered = Channel.CreateUnbounded<byte>();
        public SteppableClock Clock { get; } = new();
        public AirPlayTargetGroup Group { get; }
        public ChannelReader<byte> Rendered => _rendered.Reader;
        public Dictionary<string, ITargetSession> Sessions => Get<Dictionary<string, ITargetSession>>("_sessions");
        public int QueueDepth => (int)_reader.GetType().GetProperty("Count")!.GetValue(_reader)!;

        public Fixture()
        {
            Group = new AirPlayTargetGroup(Options.Create(new AirPlaySenderOptions()), null!, Clock,
                null!, new SelectedTargetStore(), null!, NullLogger<AirPlayTargetGroup>.Instance);
            Get<CancellationTokenSource>("_lifetime").Cancel();
            Set("_streamCts", new CancellationTokenSource());
            var queuedType = typeof(AirPlayTargetGroup).GetNestedType("QueuedFrame", BindingFlags.NonPublic)!;
            var factory = typeof(Channel).GetMethods().Single(method => method.Name == "CreateBounded"
                && method.GetParameters() is [{ ParameterType: var type }] && type == typeof(BoundedChannelOptions));
            var queue = factory.MakeGenericMethod(queuedType).Invoke(null,
                [new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true }])!;
            Set("_sendQueue", queue);
            _reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
            Group.MirrorCarriesAudio = true;
            Group.MirrorAudioSink = (pcm, _, _) =>
            {
                _rendered.Writer.TryWrite(pcm.Span[0]);
                return ValueTask.CompletedTask;
            };
        }

        public void StartSendLoop()
            => Set("_sendLoop", Invoke("SendLoopAsync", _reader, Get<CancellationTokenSource>("_streamCts").Token));

        public Task<bool> AcquireDispatchAsync(long generation)
            => (Task<bool>)Invoke("AcquireDispatchAsync", generation, Get<CancellationTokenSource>("_streamCts").Token)!;

        public Task<byte> NextRenderedAsync()
            => _rendered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        public object? Invoke(string method, params object[] args)
            => typeof(AirPlayTargetGroup).GetMethod(method, PrivateInstance)!.Invoke(Group, args);

        public T Get<T>(string field)
            => (T)typeof(AirPlayTargetGroup).GetField(field, PrivateInstance)!.GetValue(Group)!;

        public void Set(string field, object? value)
            => typeof(AirPlayTargetGroup).GetField(field, PrivateInstance)!.SetValue(Group, value);

        public ValueTask DisposeAsync() => Group.DisposeAsync();
    }

    private sealed class SteppableClock : IMasterClock
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private double _advance;
        public double NowSeconds => 1000 + _elapsed.Elapsed.TotalSeconds + Volatile.Read(ref _advance);
        public ulong NowNtp => MasterClock.ToNtp(NowSeconds);
        public ulong NtpInSeconds(double seconds) => MasterClock.ToNtp(NowSeconds + seconds);
        public void Advance(double seconds) => Volatile.Write(ref _advance, seconds);
    }

    private sealed class FakeSession : ITargetSession
    {
        public bool HoldFlush { get; init; }
        public bool ThrowOnDispose { get; init; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource FlushEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFlush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DisplayName => "fixture speaker";
        public IPEndPoint AudioEndpoint { get; } = new(IPAddress.Loopback, 0);
        public IPEndPoint ControlEndpoint { get; } = new(IPAddress.Loopback, 0);
        public int LatencyTrimMs => 0;
        public IPAddress DeviceAddress => IPAddress.Loopback;
        public bool RequiresPtp => false;
        public byte[] PrepareWirePacket(byte[] header, byte[] pcm, ushort sequence, uint timestamp) => throw new NotSupportedException();
        public void NoteRtpTime(uint timestamp) { }
        public SessionTelemetry GetTelemetry() => throw new NotSupportedException();
        public Task SetVolumeAsync(float volume, CancellationToken ct) => Task.CompletedTask;
        public Task SetMetadataAsync(TrackMetadata metadata, CancellationToken ct) => Task.CompletedTask;
        public async Task FlushAsync(ushort sequence, uint timestamp, CancellationToken ct)
        {
            FlushEntered.TrySetResult();
            if (HoldFlush) await ReleaseFlush.Task.WaitAsync(ct);
        }
        public Task TeardownAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose()
        {
            Disposed = true;
            if (ThrowOnDispose) throw new IOException("fixture broken transport");
        }
    }
}
