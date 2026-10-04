using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.AirPlay.Receiver;
using Streamsemble.AirPlay.Receiver.Rtsp;
using Streamsemble.AirPlay.Receiver.Video;
using Streamsemble.AirPlay.Sender.Raop;
using Streamsemble.Core.Metadata;
using Streamsemble.Timing.Ptp;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class ReceiverMetadataTests
{
    private static readonly TrackMetadata FirstTrack = new()
    {
        Title = "First song", Artist = "Artist", Album = "First album", TrackId = "track:1",
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NewTrackDoesNotPublishPreviousTracksArtwork(bool withTrackId)
    {
        using var incoming = await IncomingSession.CreateAsync();
        await incoming.TrackAsync(FirstTrack, withTrackId);
        await incoming.ArtAsync([1, 2, 3]);
        var previous = incoming.Latest;

        await incoming.TrackAsync(FirstTrack with
        {
            Title = "Second song", Album = "Second album", TrackId = "track:2",
        }, withTrackId);

        Assert.Equal("Second song", incoming.Latest.Title);
        Assert.NotEqual(previous.PersistentId(), incoming.Latest.PersistentId());
        Assert.Null(incoming.Latest.Artwork);
        Assert.Null(incoming.Latest.ArtworkMimeType);

        await incoming.ArtAsync([4, 5, 6]);
        Assert.Equal("Second song", incoming.Latest.Title);
        Assert.Equal(new byte[] { 4, 5, 6 }, incoming.Latest.Artwork);
    }

    [Fact]
    public async Task RepeatedTrackListingKeepsItsOwnArtwork()
    {
        using var incoming = await IncomingSession.CreateAsync();
        await incoming.TrackAsync(FirstTrack);
        await incoming.ArtAsync([1, 2, 3]);
        await incoming.TrackAsync(FirstTrack);

        Assert.Equal(new byte[] { 1, 2, 3 }, incoming.Latest.Artwork);
        Assert.Equal("image/jpeg", incoming.Latest.ArtworkMimeType);
    }

    [Fact]
    public async Task SameTrackListingCanOmitItsOptionalIdWithoutLosingArtwork()
    {
        using var incoming = await IncomingSession.CreateAsync();
        await incoming.TrackAsync(FirstTrack);
        await incoming.ArtAsync([1, 2, 3]);
        var trackId = incoming.Latest.TrackId;

        await incoming.TrackAsync(FirstTrack, withTrackId: false);

        Assert.Equal(trackId, incoming.Latest.TrackId);
        Assert.Equal(new byte[] { 1, 2, 3 }, incoming.Latest.Artwork);
    }

    [Fact]
    public async Task SameTrackIdOnlyUpdatePreservesItsTextAndArtwork()
    {
        using var incoming = await IncomingSession.CreateAsync();
        await incoming.TrackAsync(FirstTrack);
        await incoming.ArtAsync([1, 2, 3]);

        await incoming.TrackAsync(FirstTrack with { Title = null, Artist = null, Album = null });

        Assert.Equal(FirstTrack.Title, incoming.Latest.Title);
        Assert.Equal(FirstTrack.Artist, incoming.Latest.Artist);
        Assert.Equal(FirstTrack.Album, incoming.Latest.Album);
        Assert.Equal(new byte[] { 1, 2, 3 }, incoming.Latest.Artwork);
    }

    [Fact]
    public async Task DifferentTrackIdsWithIdenticalTitlesDoNotShareArtwork()
    {
        using var incoming = await IncomingSession.CreateAsync();
        await incoming.TrackAsync(FirstTrack);
        await incoming.ArtAsync([1, 2, 3]);
        var firstId = incoming.Latest.TrackId;

        await incoming.TrackAsync(FirstTrack with { TrackId = "track:different-recording" });

        Assert.NotEqual(firstId, incoming.Latest.TrackId);
        Assert.Null(incoming.Latest.Artwork);
    }

    [Fact]
    public async Task CorrectedArtworkIsPublishedWithoutChangingTrackIdentity()
    {
        using var incoming = await IncomingSession.CreateAsync();
        await incoming.TrackAsync(FirstTrack);
        await incoming.ArtAsync([1, 2, 3]);
        var original = incoming.Latest;

        await incoming.ArtAsync([4, 5, 6]);

        Assert.Equal(original.PersistentId(), incoming.Latest.PersistentId());
        Assert.NotEqual(original.ArtworkVersion(), incoming.Latest.ArtworkVersion());
        Assert.Equal(new byte[] { 4, 5, 6 }, incoming.Latest.Artwork);
    }

    [Fact]
    public async Task EmptyArtworkClearsTheCurrentCover()
    {
        using var incoming = await IncomingSession.CreateAsync();
        await incoming.TrackAsync(FirstTrack);
        await incoming.ArtAsync([1, 2, 3]);
        await incoming.ArtAsync([]);

        Assert.Null(incoming.Latest.Artwork);
        Assert.Null(incoming.Latest.ArtworkMimeType);
    }

    // Exercise the actual SET_PARAMETER handler and metadata events. The
    // connection supplies session addresses only; no audio, pairing or PTP
    // listeners are started for these tests.
    private sealed class IncomingSession(
        TcpClient sender, RtspServerConnection connection, ReceiverSession session,
        AirPlayReceiverSource source, PtpReceiverClock clock) : IDisposable
    {
        private TrackMetadata _latest = new();
        public TrackMetadata Latest => _latest;

        public static async Task<IncomingSession> CreateAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var sender = new TcpClient();
            await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var connection = new RtspServerConnection(await listener.AcceptTcpClientAsync());
            var source = new AirPlayReceiverSource();
            var clock = new PtpReceiverClock(new byte[8], NullLogger.Instance);
            var identity = new ReceiverIdentity("Test", "00:00:00:00:00:01", "test", new byte[32]);
            var session = new ReceiverSession(connection, source, new MirrorVideoSource(), identity, clock,
                66150, MirrorDisplay.Default, NullLogger.Instance);
            var incoming = new IncomingSession(sender, connection, session, source, clock);
            source.MetadataChanged += incoming.OnMetadata;
            return incoming;
        }

        private void OnMetadata(object? sender, TrackMetadata metadata) => _latest = metadata;

        public Task TrackAsync(TrackMetadata metadata, bool withTrackId = true)
        {
            if (withTrackId)
            {
                return SendAsync(Dmap.ContentType, Dmap.TrackItem(metadata));
            }

            using var body = new MemoryStream();
            foreach (var (code, value) in new[]
            {
                ("minm", metadata.Title), ("asar", metadata.Artist), ("asal", metadata.Album),
            })
            {
                var bytes = Encoding.UTF8.GetBytes(value!);
                body.Write(Encoding.ASCII.GetBytes(code));
                var length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                body.Write(length);
                body.Write(bytes);
            }

            return SendAsync(Dmap.ContentType, body.ToArray());
        }

        public Task ArtAsync(byte[] bytes) => SendAsync("image/jpeg", bytes);

        private async Task SendAsync(string contentType, byte[] body)
        {
            var reply = await session.HandleAsync(new RtspRequest(
                "SET_PARAMETER", "rtsp://test/session",
                new Dictionary<string, string> { ["Content-Type"] = contentType }, body), CancellationToken.None);
            Assert.Equal(200, reply.StatusCode);
        }

        public void Dispose()
        {
            source.MetadataChanged -= OnMetadata;
            session.Dispose();
            clock.Dispose();
            connection.Dispose();
            sender.Dispose();
        }
    }
}
