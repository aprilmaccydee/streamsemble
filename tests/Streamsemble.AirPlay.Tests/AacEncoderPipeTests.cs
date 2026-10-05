using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.AirPlay.Sender.AirPlay2;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class AacEncoderPipeTests
{
    [Fact]
    public async Task FullOutputQueue_FailsWithoutReplacingAnyAcceptedFrame()
    {
        using var stdout = new MemoryStream(AacEncoderTestStreams.Frames(AacEncoderPipe.QueueCapacity + 1));
        var failures = new List<Exception>();
        using var encoder = new AacEncoderPipe(NullLogger.Instance, Stream.Null, stdout, failures.Add);

        Assert.Single(failures);
        Assert.Equal(AacEncoderPipe.QueueCapacity + 1, encoder.FramesProduced);
        Assert.Equal(AacEncoderPipe.QueueCapacity, encoder.QueueDepth);
        Assert.Equal(1, encoder.QueueOverflows);
        Assert.Contains("queue exceeded", encoder.Failure);
        for (var i = 0; i < AacEncoderPipe.QueueCapacity; i++)
        {
            Assert.True(encoder.Frames.TryRead(out var frame));
            Assert.Equal(i, BinaryPrimitives.ReadInt32LittleEndian(frame));
        }

        Assert.Equal(0, encoder.QueueDepth);
        await Assert.ThrowsAsync<IOException>(() => encoder.Frames.Completion);
        await Assert.ThrowsAsync<IOException>(() => encoder.WritePcmAsync(new byte[1408], CancellationToken.None).AsTask());
        Assert.Equal(0, encoder.PcmSamplesIn);
    }

    [Fact]
    public async Task DisposingHealthyEncoder_CancelsReaderWithoutReportingFailure()
    {
        using var stdout = new AacEncoderTestStreams.HoldAfterStream(AacEncoderTestStreams.Frames(AacEncoderPipe.QueueCapacity));
        var failures = new List<Exception>();
        using var encoder = new AacEncoderPipe(NullLogger.Instance, Stream.Null, stdout, failures.Add);
        await stdout.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AacEncoderPipe.QueueCapacity, encoder.FramesProduced);
        Assert.Equal(AacEncoderPipe.QueueCapacity, encoder.QueueDepth);
        Assert.Equal(0, encoder.QueueOverflows);
        encoder.Dispose();
        await stdout.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(encoder.Failure);
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedOutputEndOrFault_ReportsFailure(bool fault)
    {
        using Stream stdout = fault ? new FailingOutputStream() : new MemoryStream();
        var failures = new List<Exception>();
        using var encoder = new AacEncoderPipe(NullLogger.Instance, Stream.Null, stdout, failures.Add);

        var failure = Assert.Single(failures);
        Assert.Equal(fault ? "stdout failed" : "AAC encoder output ended unexpectedly", failure.Message);
        Assert.Equal(failure.Message, encoder.Failure);
        Assert.Equal(0, encoder.QueueOverflows);
        await Assert.ThrowsAnyAsync<IOException>(() => encoder.Frames.Completion);
    }

    [Fact]
    public async Task Failure_CancelsBlockedInputAsLocalIoFailure()
    {
        using var stdout = new AacEncoderTestStreams.HoldAfterStream([]);
        using var stdin = new BlockedInputStream();
        var failures = new List<Exception>();
        using var encoder = new AacEncoderPipe(NullLogger.Instance, stdin, stdout, failures.Add);
        var write = encoder.WritePcmAsync(new byte[1408], CancellationToken.None).AsTask();
        await stdin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        encoder.Abort(new IOException("expired timeline"));

        var exception = await Assert.ThrowsAsync<IOException>(() => write.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("expired timeline", exception.InnerException!.Message);
        Assert.Equal(0, encoder.PcmSamplesIn);
        Assert.Single(failures);
        await stdout.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FailingOutputStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("stdout failed"));
    }

    internal sealed class BlockedInputStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
