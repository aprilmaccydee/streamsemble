using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.AirPlay.Common;
using Streamsemble.AirPlay.Sender.AirPlay2;
using Streamsemble.Timing.Ptp;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class BufferedPlaybackHealthTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1.0)]
    public void ExpiredLead_RequiresContinuousFailureAndResetsWhenInactiveOrRecovered(double? recoveredLead)
    {
        var health = new BufferedPlaybackHealth();
        Assert.False(health.ShouldReconnect(-4200, 0));
        Assert.False(health.ShouldReconnect(-4200, 1_999_999_999));
        Assert.False(health.ShouldReconnect(recoveredLead, 2_000_000_000));
        Assert.False(health.ShouldReconnect(-4200, 3_000_000_000));
        Assert.False(health.ShouldReconnect(-4200, 4_999_999_999));
        Assert.True(health.ShouldReconnect(-4200, 5_000_000_000));
    }

    [Fact]
    public async Task ExpiredSession_ReleasesBlockedEncoderInputAndBecomesReconnectable()
    {
        using var stdout = new AacEncoderTestStreams.HoldAfterStream([]);
        using var stdin = new AacEncoderPipeTests.BlockedInputStream();
        using var encoder = new AacEncoderPipe(NullLogger.Instance, stdin, stdout);
        using var session = ExpiredSession();
        Set(session, "_aac", encoder);
        var write = session.WritePcmAsync(new byte[1408], 0, CancellationToken.None).AsTask();
        await stdin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.CheckBufferedPlayback(0);
        Assert.True(session.IsAlive);
        session.CheckBufferedPlayback(BufferedPlaybackHealth.ExpiredLeadGraceNanos);

        Assert.False(session.IsAlive);
        Assert.Contains("render deadline", encoder.Failure);
        await Assert.ThrowsAsync<IOException>(() => write.WaitAsync(TimeSpan.FromSeconds(5)));
        // Further fan-out calls skip the dead pipeline instead of blocking.
        await session.WritePcmAsync(new byte[1408], 352, CancellationToken.None);
    }

    [Theory]
    [InlineData("_anchored", false)]
    [InlineData("_anchorHeld", true)]
    [InlineData("_reanchorPending", true)]
    public void InactiveAnchor_DoesNotTriggerExpiredTimelineRecovery(string field, bool value)
    {
        using var session = ExpiredSession();
        Set(session, field, value);
        session.CheckBufferedPlayback(0);
        session.CheckBufferedPlayback(10_000_000_000);
        Assert.True(session.IsAlive);
    }

    [Fact]
    public void EncoderEof_MarksSessionDeadWithoutWaitingForAnotherPcmWrite()
    {
        using var session = ExpiredSession();
        using var stdout = new MemoryStream();
        using var encoder = new AacEncoderPipe(NullLogger.Instance, Stream.Null, stdout,
            error => session.RequestBufferedReconnect(error.Message));
        Assert.False(session.IsAlive);
        Assert.Equal("AAC encoder output ended unexpectedly", encoder.Failure);
    }

    [Fact]
    public async Task FullAlacQueue_FailsSessionInsteadOfSilentlyDiscardingContent()
    {
        using var session = ExpiredSession();
        Set(session, "_bufferedAlac", true);
        var pcm = new byte[1408];
        for (var i = 0; i < 256; i++)
        {
            await session.WritePcmAsync(pcm, i * 352, CancellationToken.None);
        }

        Assert.True(session.IsAlive);
        await session.WritePcmAsync(pcm, 256 * 352, CancellationToken.None);
        Assert.False(session.IsAlive);
    }

    [Fact]
    public async Task UnexpectedTransportCancellation_DoesNotLeaveStoppedPumpAlive()
    {
        using var session = ExpiredSession();
        Set(session, "_audioCipher", new AirPlay2AudioCipher(new byte[32]));
        Set(session, "_bufferedAlac", true);
        await session.WritePcmAsync(new byte[1408], 0, CancellationToken.None);
        using var output = new CancelledOutputStream();
        var pump = (Task)typeof(AirPlay2Session)
            .GetMethod("BufferedPumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [output, CancellationToken.None])!;
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.IsAlive);
    }

    [Fact]
    public async Task UnusableSharedAnchor_RequestsCoordinatedRecoveryWithoutReplacingEpoch()
    {
        using var session = ExpiredSession();
        var epoch = new GroupTimelineAnchor();
        epoch.Map(0, 10_000_000_000);
        var original = epoch.Snapshot();
        session.GroupAnchor = epoch;
        session.AnchorClock = () => (20_000_000_000, new byte[8]);
        string? recoveryReason = null;
        session.RequestGroupRecovery = reason => recoveryReason = reason;

        var anchor = (Task<bool>)typeof(AirPlay2Session)
            .GetMethod("SendAnchorAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [0u, 0L, 0L, CancellationToken.None])!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => anchor);
        Assert.Contains("playback lead", recoveryReason);
        Assert.Equal(original, epoch.Snapshot());
    }

    private static AirPlay2Session ExpiredSession()
    {
        var session = new AirPlay2Session("test", IPAddress.Loopback, 7000, 0, NullLogger.Instance);
        typeof(AirPlay2Session).GetProperty(nameof(AirPlay2Session.IsBuffered))!.SetValue(session, true);
        Set(session, "_anchored", true);
        Set(session, "_lastAnchorNanos", (ulong)(PtpReceiverClock.NowNanos - 5_000_000_000));
        return session;
    }

    private static void Set(object target, string field, object value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class CancelledOutputStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new OperationCanceledException("transport cancelled independently"));
    }
}
