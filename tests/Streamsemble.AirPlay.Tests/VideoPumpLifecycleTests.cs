using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.Core.Video;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class VideoPumpLifecycleTests
{
    [Fact]
    public async Task StopDuringConnectionDiscardsOldReplayAndStartsTheNextStreamAfterTeardown()
    {
        var source = new RestartableSource();
        var sink = new ControlledSink { HoldFirstConnection = true, HoldFirstStop = true };
        await using var pump = new VideoPump(source, sink, NullLogger<VideoPump>.Instance);
        source.Push(1, keyframe: true);
        source.Push(2);
        pump.Start(CancellationToken.None);
        await sink.FirstConnectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        source.Stop();
        source.Restart();
        source.Push(10, keyframe: true);
        source.Push(11);
        sink.AllowFirstConnection.TrySetResult();
        try
        {
            await sink.FirstStopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // The new keyframe is ready, but its session must not start while
            // the old session's teardown can still close the output.
            Assert.Equal(1, sink.Starts);
            Assert.Empty(sink.Written);
        }
        finally
        {
            // Teardown deliberately has no caller cancellation. Release it
            // even if an assertion fails, so fixture disposal cannot hang.
            sink.AllowFirstStop.TrySetResult();
        }
        await sink.SecondStreamWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, sink.Starts);
        Assert.Equal(new long[] { 10, 11 }, sink.Written.ToArray());
        Assert.Equal(new[] { "start:1", "stop:1", "start:2", "frame:10", "frame:11" }, sink.Events.ToArray());
    }

    [Fact]
    public async Task StopDuringReplayCannotRestoreOldConfigOrSwallowTheNextKeyframe()
    {
        var source = new RestartableSource();
        var sink = new ControlledSink { HoldFirstFrame = true };
        await using var pump = new VideoPump(source, sink, NullLogger<VideoPump>.Instance);
        source.Push(1, keyframe: true);
        source.Push(2);
        source.Push(3);
        pump.Start(CancellationToken.None);
        await sink.FirstFrameStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        source.Stop();
        source.Restart();
        source.Push(10, keyframe: true);
        source.Push(11);
        sink.AllowFirstFrame.TrySetResult();
        await sink.SecondStreamWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Frame 1 was already in flight. Frames 2/3 were held only by the old
        // replay and must not reconfigure or write after that source stops.
        Assert.Equal(2, sink.Starts);
        Assert.Equal(new long[] { 1, 10, 11 }, sink.Written.ToArray());
        Assert.DoesNotContain(sink.Events, entry => entry.StartsWith("config:"));
        Assert.Equal(new[] { "start:1", "frame:1", "stop:1", "start:2", "frame:10", "frame:11" }, sink.Events.ToArray());
    }

    private sealed class RestartableSource : IVideoSource
    {
        private readonly Channel<VideoFrame> _frames = Channel.CreateUnbounded<VideoFrame>();
        public string Name => "restartable mirror";
        public bool IsActive { get; private set; } = true;
        public VideoCodecConfig? CodecConfig { get; private set; } = Config();
        public ChannelReader<VideoFrame> Frames => _frames.Reader;
        public event EventHandler<VideoCodecConfig>? CodecConfigChanged { add { } remove { } }
        public event EventHandler<bool>? ActiveChanged;

        public void Push(long stamp, bool keyframe = false)
            => _frames.Writer.TryWrite(new VideoFrame(
                new byte[] { 0, 0, 0, 1, keyframe ? (byte)0x65 : (byte)0x41 }, stamp, keyframe)
            {
                CodecConfig = CodecConfig,
            });

        public void Stop()
        {
            IsActive = false;
            while (_frames.Reader.TryRead(out _)) { }
            ActiveChanged?.Invoke(this, false);
        }

        public void Restart()
        {
            CodecConfig = Config();
            IsActive = true;
            ActiveChanged?.Invoke(this, true);
        }

        private static VideoCodecConfig Config()
            => new([0x67, 0x64, 0, 0x28], [0x68, 0xEE], 1920, 1080);
    }

    private sealed class ControlledSink : IVideoSink
    {
        private int _starts;
        private int _stops;
        public bool HoldFirstConnection { get; init; }
        public bool HoldFirstFrame { get; init; }
        public bool HoldFirstStop { get; init; }
        public int Starts => Volatile.Read(ref _starts);
        public TaskCompletionSource FirstConnectionStarted { get; } = Signal();
        public TaskCompletionSource AllowFirstConnection { get; } = Signal();
        public TaskCompletionSource FirstFrameStarted { get; } = Signal();
        public TaskCompletionSource AllowFirstFrame { get; } = Signal();
        public TaskCompletionSource FirstStopStarted { get; } = Signal();
        public TaskCompletionSource AllowFirstStop { get; } = Signal();
        public TaskCompletionSource SecondStreamWritten { get; } = Signal();
        public ConcurrentQueue<long> Written { get; } = new();
        public ConcurrentQueue<string> Events { get; } = new();

        public async Task StartStreamAsync(VideoCodecConfig config, CancellationToken ct = default)
        {
            var starts = Interlocked.Increment(ref _starts);
            Events.Enqueue($"start:{starts}");
            if (starts == 1)
            {
                FirstConnectionStarted.TrySetResult();
                if (HoldFirstConnection)
                {
                    await AllowFirstConnection.Task.WaitAsync(ct);
                }
            }
        }

        public Task ReconfigureAsync(VideoCodecConfig config, CancellationToken ct = default)
        {
            Events.Enqueue($"config:{config.Width}");
            return Task.CompletedTask;
        }

        public async ValueTask WriteAsync(VideoFrame frame, CancellationToken ct = default)
        {
            if (frame.TargetNanos == 1)
            {
                FirstFrameStarted.TrySetResult();
                if (HoldFirstFrame)
                {
                    await AllowFirstFrame.Task.WaitAsync(ct);
                }
            }

            Events.Enqueue($"frame:{frame.TargetNanos}");
            Written.Enqueue(frame.TargetNanos);
            if (frame.TargetNanos == 11)
            {
                SecondStreamWritten.TrySetResult();
            }
        }

        public async Task StopStreamAsync(CancellationToken ct = default)
        {
            var stops = Interlocked.Increment(ref _stops);
            Events.Enqueue($"stop:{stops}");
            if (stops == 1)
            {
                FirstStopStarted.TrySetResult();
                if (HoldFirstStop)
                {
                    await AllowFirstStop.Task.WaitAsync(ct);
                }
            }
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
