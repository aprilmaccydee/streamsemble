using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.Core.Video;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class VideoPumpConfigTests
{
    [Fact]
    public async Task ResizeDuringConnectionConfiguresTheDecoderBeforeReplayingItsKeyframe()
    {
        var original = Config(1920);
        var resized = Config(1280);
        var source = new QueuedSource { CodecConfig = original };
        var sink = new ConnectingSink();
        await using var pump = new VideoPump(source, sink, NullLogger<VideoPump>.Instance);
        source.Push(Frame(1, original, keyframe: true));
        pump.Start(CancellationToken.None);
        await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        source.CodecConfig = resized;
        source.Push(Frame(2, original));
        source.Push(Frame(3, resized, keyframe: true));
        source.Push(Frame(4, resized));
        sink.Connected.TrySetResult();

        var first = await sink.NextFrameAsync();
        var second = await sink.NextFrameAsync();
        Assert.Equal(3, first.Frame.TargetNanos);
        Assert.True(first.Frame.IsKeyframe);
        Assert.Same(resized, first.Config);
        Assert.Equal(4, second.Frame.TargetNanos);
        Assert.Same(resized, second.Config);
        Assert.Equal(new[] { "start:1920", "config:1280", "frame:3", "frame:4" }, sink.Events.ToArray());
    }

    [Fact]
    public async Task QueuedFramesKeepTheirConfigEvenWhenTheSourceAlreadyHasANewerOne()
    {
        var original = Config(1920);
        var resized = Config(1280);
        var source = new QueuedSource { CodecConfig = resized };
        var sink = new ConnectingSink();
        sink.Connected.TrySetResult();
        await using var pump = new VideoPump(source, sink, NullLogger<VideoPump>.Instance);
        source.Push(Frame(1, original, keyframe: true));
        source.Push(Frame(2, original));
        pump.Start(CancellationToken.None);

        var first = await sink.NextFrameAsync();
        var second = await sink.NextFrameAsync();
        Assert.Same(original, first.Config);
        Assert.Same(original, second.Config);
        Assert.Equal(new[] { "start:1920", "frame:1", "frame:2" }, sink.Events.ToArray());

        source.Push(Frame(3, resized, keyframe: true));
        var third = await sink.NextFrameAsync();
        Assert.Same(resized, third.Config);
        Assert.Equal(new[] { "start:1920", "frame:1", "frame:2", "config:1280", "frame:3" }, sink.Events.ToArray());
    }

    private static VideoCodecConfig Config(int width)
        => new([0x67, 0x64, 0x00, 0x28], [0x68, 0xEE], width, 1080);

    private static VideoFrame Frame(long stamp, VideoCodecConfig config, bool keyframe = false)
        => new(new byte[] { 0, 0, 0, 1, keyframe ? (byte)0x65 : (byte)0x41 }, stamp, keyframe)
        {
            CodecConfig = config,
        };

    private sealed class QueuedSource : IVideoSource
    {
        private readonly Channel<VideoFrame> _frames = Channel.CreateUnbounded<VideoFrame>();
        public string Name => "mirror config fixture";
        public bool IsActive => true;
        public VideoCodecConfig? CodecConfig { get; set; }
        public ChannelReader<VideoFrame> Frames => _frames.Reader;
        public event EventHandler<VideoCodecConfig>? CodecConfigChanged { add { } remove { } }
        public event EventHandler<bool>? ActiveChanged { add { } remove { } }
        public void Push(VideoFrame frame) => _frames.Writer.TryWrite(frame);
    }

    private sealed class ConnectingSink : IVideoSink
    {
        private readonly Channel<(VideoFrame Frame, VideoCodecConfig Config)> _written =
            Channel.CreateUnbounded<(VideoFrame, VideoCodecConfig)>();
        private VideoCodecConfig? _config;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Events { get; } = new();

        public async Task StartStreamAsync(VideoCodecConfig config, CancellationToken ct = default)
        {
            _config = config;
            Events.Enqueue($"start:{config.Width}");
            Started.TrySetResult();
            await Connected.Task.WaitAsync(ct);
        }

        public Task ReconfigureAsync(VideoCodecConfig config, CancellationToken ct = default)
        {
            _config = config;
            Events.Enqueue($"config:{config.Width}");
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync(VideoFrame frame, CancellationToken ct = default)
        {
            Events.Enqueue($"frame:{frame.TargetNanos}");
            _written.Writer.TryWrite((frame, _config!));
            return ValueTask.CompletedTask;
        }

        public Task<(VideoFrame Frame, VideoCodecConfig Config)> NextFrameAsync()
            => _written.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        public Task StopStreamAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
