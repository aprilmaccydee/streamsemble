using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.AirPlay.Sender;
using Streamsemble.Core.Metadata;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class MetadataFanoutTests
{
    [Fact]
    public async Task SlowSpeakerDoesNotDelayOtherSpeakersOrTheirNextUpdate()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<TrackMetadata>();
        ITargetSession[] sessions =
        [
            new FakeSession((_, ct) => release.Task.WaitAsync(ct)),
            new FakeSession((metadata, _) =>
            {
                received.Add(metadata);
                return Task.CompletedTask;
            }),
        ];
        var firstTrack = new TrackMetadata { Title = "First" };
        var secondTrack = new TrackMetadata { Title = "Second" };

        var first = SendAsync(sessions, firstTrack);
        var second = SendAsync(sessions, secondTrack);
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(new[] { firstTrack, secondTrack }, received);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second);
        }
    }

    [Fact]
    public async Task FailingSpeakerDoesNotPreventOtherSpeakersReceivingMetadata()
    {
        TrackMetadata? received = null;
        ITargetSession[] sessions =
        [
            new FakeSession((_, _) => throw new IOException("Speaker disconnected")),
            new FakeSession((metadata, _) =>
            {
                received = metadata;
                return Task.CompletedTask;
            }),
        ];
        var track = new TrackMetadata { Title = "Current track" };

        await SendAsync(sessions, track);

        Assert.Same(track, received);
    }

    [Fact]
    public async Task CancellationIsNotSwallowedAsASpeakerFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ITargetSession[] sessions =
        [
            new FakeSession((_, ct) => Task.FromCanceled(ct)),
        ];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AirPlayTargetGroup.SendMetadataToSessionsAsync(
                sessions, new TrackMetadata(), NullLogger.Instance, cancellation.Token));
    }

    private static Task SendAsync(IEnumerable<ITargetSession> sessions, TrackMetadata metadata) =>
        AirPlayTargetGroup.SendMetadataToSessionsAsync(
            sessions, metadata, NullLogger.Instance, CancellationToken.None);

    private sealed class FakeSession(Func<TrackMetadata, CancellationToken, Task> sendMetadata) : ITargetSession
    {
        public string DisplayName => "Test speaker";
        public IPEndPoint AudioEndpoint { get; } = new(IPAddress.Loopback, 0);
        public IPEndPoint ControlEndpoint { get; } = new(IPAddress.Loopback, 0);
        public int LatencyTrimMs => 0;
        public IPAddress DeviceAddress => IPAddress.Loopback;
        public bool RequiresPtp => false;
        public byte[] PrepareWirePacket(byte[] rtpHeader, byte[] pcm, ushort sequenceNumber, uint rtpTimestamp) =>
            throw new NotSupportedException();
        public void NoteRtpTime(uint rtpTime) { }
        public SessionTelemetry GetTelemetry() => throw new NotSupportedException();
        public Task SetVolumeAsync(float volume, CancellationToken ct) => Task.CompletedTask;
        public Task SetMetadataAsync(TrackMetadata metadata, CancellationToken ct) => sendMetadata(metadata, ct);
        public Task FlushAsync(ushort nextSeq, uint nextRtpTime, CancellationToken ct) => Task.CompletedTask;
        public Task TeardownAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }
}
