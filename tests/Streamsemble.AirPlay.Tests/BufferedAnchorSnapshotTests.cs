using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.AirPlay.Common;
using Streamsemble.AirPlay.Sender.AirPlay2;
using Streamsemble.Core.Audio;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class BufferedAnchorSnapshotTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1_051_776L)] // A speaker joining an existing capture timeline.
    public async Task InFlightPcmWrite_KeepsTheSameEncodedFrameOnItsCaptureSample(long captureOrigin)
    {
        var settled = await ObserveAnchorAsync(inFlightWrite: false, captureOrigin);
        var writing = await ObserveAnchorAsync(inFlightWrite: true, captureOrigin);

        // AU 44 has identical content regardless of the next stdin write.
        // Before the gate, the in-flight write mapped it 352 samples (~8 ms)
        // later, independently for each speaker's encoder.
        Assert.Equal(captureOrigin + 44 * 1024 - AacEncoderPipe.EncoderDelaySamples, settled);
        Assert.Equal(settled, writing);
    }

    [Fact]
    public async Task CancelledPump_DoesNotWaitForAnInFlightPcmWrite()
    {
        Assert.Equal(-1, await ObserveAnchorAsync(inFlightWrite: true, captureOrigin: 0, cancelPump: true));
    }

    private static async Task<long> ObserveAnchorAsync(bool inFlightWrite, long captureOrigin, bool cancelPump = false)
    {
        using var stdin = new GatedStream();
        using var process = new Process();
        // Inject a redirected stdin stream without launching ffmpeg. Both
        // public PCM writes and the private buffered pump below remain the
        // production methods, including their independent sample counters.
        Set(process, "_standardInput", new StreamWriter(stdin));

        var frames = Channel.CreateUnbounded<byte[]>();
        for (var i = 0; i < 45; i++) frames.Writer.TryWrite([1]);
        frames.Writer.TryComplete();

        var encoder = (AacEncoderPipe)RuntimeHelpers.GetUninitializedObject(typeof(AacEncoderPipe));
        Set(encoder, "_process", process);
        Set(encoder, "_logger", NullLogger.Instance);
        Set(encoder, "_frames", frames);
        Set(encoder, "_pcmBytesIn", 48_000L * 4);

        var session = new AirPlay2Session("repro", IPAddress.Loopback, 7000, 0, NullLogger.Instance);
        Set(session, "_aac", encoder);
        Set(session, "_audioCipher", new AirPlay2AudioCipher(new byte[32]));
        Set(session, "_pcmCaptureEnd", captureOrigin + 48_000L);
        session.AnchorClock = () => (1_000_000_000_000UL, new byte[8]);
        long observedCapture = -1;
        var inputWasUnlockedAtAnchor = false;
        Task callbackWrite = Task.CompletedTask;
        session.TargetNanosForCapture = capture =>
        {
            observedCapture = capture;
            // An anchor's RTSP/timing work must not retain the input gate.
            callbackWrite = session.WritePcmAsync(new byte[PcmFrame.CanonicalFrameBytes],
                captureOrigin + 48_000 + (inFlightWrite ? PcmFrame.SamplesPerFrame : 0),
                CancellationToken.None).AsTask();
            inputWasUnlockedAtAnchor = callbackWrite.IsCompletedSuccessfully;
            throw new InvalidOperationException("Stop after observing the production anchor calculation; no RTSP peer is needed");
        };

        var write = inFlightWrite
            ? session.WritePcmAsync(new byte[PcmFrame.CanonicalFrameBytes], captureOrigin + 48_000, CancellationToken.None).AsTask()
            : Task.CompletedTask;
        if (inFlightWrite)
        {
            await stdin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(write.IsCompleted);
            Assert.Equal(48_000, encoder.PcmSamplesIn);
        }
        else
        {
            stdin.Release.TrySetResult();
        }

        using var output = new MemoryStream();
        using var pumpCts = new CancellationTokenSource();
        var pump = (Task)typeof(AirPlay2Session)
            .GetMethod("BufferedPumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [output, pumpCts.Token])!;
        try
        {
            if (inFlightWrite)
            {
                Assert.False(pump.IsCompleted);
                Assert.Equal(-1, observedCapture);
            }

            if (cancelPump)
            {
                pumpCts.Cancel();
            }
            else
            {
                stdin.Release.TrySetResult();
            }

            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            stdin.Release.TrySetResult();
            await write.WaitAsync(TimeSpan.FromSeconds(5));
            await callbackWrite.WaitAsync(TimeSpan.FromSeconds(5));
        }

        if (!cancelPump)
        {
            Assert.True(observedCapture >= 0);
            Assert.True(inputWasUnlockedAtAnchor);
        }

        return observedCapture;
    }

    private static void Set(object target, string field, object value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private sealed class GatedStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
