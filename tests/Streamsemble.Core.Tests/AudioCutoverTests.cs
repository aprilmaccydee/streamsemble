using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Audio;
using Streamsemble.Core.Metadata;
using Xunit;

namespace Streamsemble.Core.Tests;

public class AudioCutoverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceCutoverInvalidatesHeldFramesAndKeepsOnlyTheFreshGeneration(bool prepareExplicitly)
    {
        var source = new TestSource();
        source.Push(1);
        Assert.True(source.Frames.TryRead(out var held));
        source.Push(2);

        source.Cutover(prepareExplicitly);
        source.Push(3);

        Assert.True(source.Frames.TryRead(out var fresh));
        Assert.False(source.Frames.TryRead(out _));
        Assert.NotEqual(held.Generation, source.Generation);
        Assert.Equal(source.Generation, fresh.Generation);
        Assert.Equal(3, Id(fresh));
        Assert.Equal(2 * PcmFrame.SamplesPerFrame, fresh.Timestamp);
    }

    [Fact]
    public void OrdinaryPausePreservesCompletedPcmAndItsGeneration()
    {
        var source = new TestSource();
        source.Push(1);
        var generation = source.Generation;
        source.Pause();
        source.Push(2);

        Assert.Equal(generation, source.Generation);
        Assert.True(source.Frames.TryRead(out var first));
        Assert.True(source.Frames.TryRead(out var second));
        Assert.Equal(1, Id(first));
        Assert.Equal(2, Id(second));
        Assert.Equal(generation, first.Generation);
        Assert.Equal(generation, second.Generation);
    }

    [Fact]
    public async Task InHandOldFrameIsRejectedAndFirstFreshFrameWaitsForCutover()
    {
        var source = new TestSource();
        var sink = new ControlledSink();
        var freshRead = Signal();
        source.FrameRead = frame =>
        {
            if (Id(frame) == 1)
            {
                // The channel already handed this frame to the pump. Its
                // generation must still prevent a write after the cutover.
                source.Cutover();
                source.Push(3);
            }
            else if (Id(frame) == 3)
            {
                freshRead.TrySetResult();
            }
        };
        source.Push(1);
        source.Push(2);
        await using var pump = Pump(source, sink);
        pump.Start(CancellationToken.None);

        var cutover = await sink.NextCutoverAsync();
        await freshRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(sink.Written);
        cutover.TrySetResult();

        var written = await sink.NextWriteAsync();
        Assert.Equal(3, Id(written));
        Assert.Equal(source.Generation, written.Generation);
        Assert.Single(sink.Written);
        Assert.Equal(new[] { "flush-start", "flush-end", "resume", "write:3" }, sink.Events.ToArray());
    }

    [Fact]
    public async Task ASecondCutoverInvalidatesAFrameAlreadyWaitingForTheFirstBarrier()
    {
        var source = new TestSource();
        var sink = new ControlledSink();
        var firstFreshRead = Signal();
        source.FrameRead = frame =>
        {
            if (Id(frame) == 10) firstFreshRead.TrySetResult();
        };
        await using var pump = Pump(source, sink);
        pump.Start(CancellationToken.None);

        source.Cutover();
        var firstCutover = await sink.NextCutoverAsync();
        source.Push(10);
        await firstFreshRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.Cutover();
        source.Push(20);
        firstCutover.TrySetResult();

        var secondCutover = await sink.NextCutoverAsync();
        Assert.Empty(sink.Written);
        secondCutover.TrySetResult();
        Assert.Equal(20, Id(await sink.NextWriteAsync()));
        Assert.Single(sink.Written);
    }

    [Fact]
    public async Task PauseDuringCutoverPreventsAutomaticResume()
    {
        var source = new TestSource();
        var sink = new ControlledSink();
        await using var pump = Pump(source, sink);
        pump.Start(CancellationToken.None);

        source.Cutover();
        var cutover = await sink.NextCutoverAsync();
        source.Pause();
        source.Push(7);
        cutover.TrySetResult();

        Assert.Equal(7, Id(await sink.NextWriteAsync()));
        Assert.DoesNotContain("resume", sink.Events);
        Assert.Equal(new[] { "flush-start", "flush-end", "pause", "write:7" }, sink.Events.ToArray());
    }

    private static AudioPump Pump(TestSource source, ControlledSink sink)
        => new(new FixedArbiter(source), sink, new PlaybackStatus(), NullLogger<AudioPump>.Instance);

    private static byte Id(PcmFrame frame) => frame.Data.Span[0];

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TestSource : AudioSourceBase, IAudioSource
    {
        private readonly ChannelReader<PcmFrame> _observedFrames;

        public TestSource() : base("cutover fixture")
        {
            SetState(SourceState.Active);
            _observedFrames = new ObservedReader(base.Frames, frame => FrameRead?.Invoke(frame));
        }

        public Action<PcmFrame>? FrameRead { get; set; }
        ChannelReader<PcmFrame> IAudioSource.Frames => _observedFrames;

        public void Push(byte id)
        {
            var bytes = new byte[PcmFrame.CanonicalFrameBytes];
            Array.Fill(bytes, id);
            EmitPcm(bytes);
        }

        public void Cutover(bool prepareExplicitly = true)
        {
            if (prepareExplicitly) DiscardQueuedPcm();
            RaiseDiscontinuity();
        }

        public void Pause() => SetState(SourceState.Paused);

        public override Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ObservedReader(ChannelReader<PcmFrame> inner, Action<PcmFrame> onRead) : ChannelReader<PcmFrame>
    {
        public override Task Completion => inner.Completion;
        public override ValueTask<bool> WaitToReadAsync(CancellationToken ct = default) => inner.WaitToReadAsync(ct);
        public override bool TryRead(out PcmFrame frame)
        {
            if (!inner.TryRead(out frame)) return false;
            onRead(frame);
            return true;
        }
    }

    private sealed class FixedArbiter(IAudioSource source) : ISourceArbiter
    {
        public IAudioSource? ActiveSource => source;
        public event EventHandler<IAudioSource?>? ActiveSourceChanged { add { } remove { } }
        public void Register(IAudioSource registered) { }
    }

    private sealed class ControlledSink : IAudioSink
    {
        private readonly Channel<TaskCompletionSource> _cutovers = Channel.CreateUnbounded<TaskCompletionSource>();
        private readonly Channel<PcmFrame> _writes = Channel.CreateUnbounded<PcmFrame>();
        public ConcurrentQueue<PcmFrame> Written { get; } = new();
        public ConcurrentQueue<string> Events { get; } = new();

        public Task StartStreamAsync(AudioFormat format, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopStreamAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetVolumeAsync(float volume, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetMetadataAsync(TrackMetadata metadata, CancellationToken ct = default) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task FlushAsync(bool dropQueuedAudio, CancellationToken ct = default)
        {
            if (!dropQueuedAudio)
            {
                Events.Enqueue("pause");
                return;
            }

            var release = Signal();
            Events.Enqueue("flush-start");
            _cutovers.Writer.TryWrite(release);
            await release.Task.WaitAsync(ct);
            Events.Enqueue("flush-end");
        }

        public Task ResumeAsync(CancellationToken ct = default)
        {
            Events.Enqueue("resume");
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync(PcmFrame frame, CancellationToken ct = default)
        {
            Events.Enqueue($"write:{Id(frame)}");
            Written.Enqueue(frame);
            _writes.Writer.TryWrite(frame);
            return ValueTask.CompletedTask;
        }

        public Task<TaskCompletionSource> NextCutoverAsync()
            => _cutovers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        public Task<PcmFrame> NextWriteAsync()
            => _writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }
}
