using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.AirPlay.Receiver.Video;
using Streamsemble.Core.Video;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class VideoSourceBufferTests
{
    [Fact]
    public async Task SlowConnectionPreservesEveryFrameInTheOpeningReferenceChain()
    {
        var source = CreateSource();
        var sink = new DelayedSink(expectedFrames: 301);
        await source.PushAccessUnitAsync(Frame(0));
        await using var pump = new VideoPump(source, sink, NullLogger<VideoPump>.Instance);
        pump.Start(CancellationToken.None);
        await sink.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var producer = SendRemainingFramesAsync();
        try
        {
            // The opening IDR is held by the pump during connection. More
            // than 256 P-frames must stall the socket reader, never silently
            // discard the middle of that IDR's decoder reference chain.
            Assert.False(producer.IsCompleted);
        }
        finally
        {
            sink.AllowConnection.TrySetResult();
        }

        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        await sink.AllFramesWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0, 301).Select(i => (long)i), sink.Written.Select(f => f.TargetNanos));
        Assert.True(sink.Written.First().IsKeyframe);
        Assert.All(sink.Written.Skip(1), frame => Assert.False(frame.IsKeyframe));

        async Task SendRemainingFramesAsync()
        {
            for (var i = 1; i <= 300; i++)
            {
                await source.PushAccessUnitAsync(Frame(i));
            }
        }
    }

    [Fact]
    public async Task CancellingABlockedWritePreservesTheFramesAlreadyQueued()
    {
        var source = CreateSource();
        for (var i = 0; i < 256; i++)
        {
            await source.PushAccessUnitAsync(Frame(i));
        }

        using var cts = new CancellationTokenSource();
        var blocked = source.PushAccessUnitAsync(Frame(256), cts.Token).AsTask();
        Assert.False(blocked.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => blocked.WaitAsync(TimeSpan.FromSeconds(5)));

        var retained = new List<long>();
        while (source.Frames.TryRead(out var frame))
        {
            retained.Add(frame.TargetNanos);
        }

        Assert.Equal(Enumerable.Range(0, 256).Select(i => (long)i), retained);
        Assert.Equal(256, source.FramesEmitted);
    }

    [Fact]
    public async Task FrameCapturesItsCodecBeforeWaitingForBufferSpace()
    {
        var source = CreateSource();
        var originalConfig = source.CodecConfig;
        for (var i = 0; i < 256; i++)
        {
            await source.PushAccessUnitAsync(Frame(i));
        }

        var blocked = source.PushAccessUnitAsync(Frame(256)).AsTask();
        Assert.False(blocked.IsCompleted);
        var newConfig = new VideoCodecConfig([0x67, 0x64, 0, 0x28], [0x68, 0xEE], 1280, 720);
        source.PushCodecConfig(newConfig);
        Assert.True(source.Frames.TryRead(out _));
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));

        var retained = new List<VideoFrame>();
        while (source.Frames.TryRead(out var frame))
        {
            retained.Add(frame);
        }

        Assert.Equal(256, retained.Count);
        Assert.All(retained, frame => Assert.Same(originalConfig, frame.CodecConfig));
        await source.PushAccessUnitAsync(Frame(257));
        Assert.True(source.Frames.TryRead(out var next));
        Assert.Same(newConfig, next.CodecConfig);
    }

    private static MirrorVideoSource CreateSource()
    {
        var source = new MirrorVideoSource();
        source.PushCodecConfig(new VideoCodecConfig([0x67, 0x64, 0, 0x28], [0x68, 0xEE], 1920, 1080));
        source.MarkActive();
        return source;
    }

    private static VideoFrame Frame(long number)
        => new(new byte[] { 0, 0, 0, 1, number == 0 ? (byte)0x65 : (byte)0x41 }, number, number == 0);

    private sealed class DelayedSink(int expectedFrames) : IVideoSink
    {
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowConnection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllFramesWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<VideoFrame> Written { get; } = new();

        public async Task StartStreamAsync(VideoCodecConfig config, CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult();
            await AllowConnection.Task.WaitAsync(cancellationToken);
        }

        public Task ReconfigureAsync(VideoCodecConfig config, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask WriteAsync(VideoFrame frame, CancellationToken cancellationToken = default)
        {
            Written.Enqueue(frame);
            if (Written.Count == expectedFrames)
            {
                AllFramesWritten.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        public Task StopStreamAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
