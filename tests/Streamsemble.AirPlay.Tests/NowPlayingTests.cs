using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.AirPlay.Sender.Raop;
using Streamsemble.Core.Metadata;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

/// <summary>
/// Drives the real <see cref="NowPlaying"/> path over a real
/// <see cref="RtspClient"/> against a stand-in receiver, so what is asserted is
/// what a speaker would actually receive on the wire.
/// </summary>
public class NowPlayingTests
{
    private sealed record Request(string Method, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        public string? ContentType => Headers.GetValueOrDefault("Content-Type");
        public string BodyText => Encoding.UTF8.GetString(Body);
    }

    /// <summary>Answers every request with 200 OK and records what it was sent.</summary>
    private sealed class FakeReceiver : IDisposable
    {
        private readonly TcpListener _listener;
        public List<Request> Received { get; } = [];

        /// <summary>Content types this receiver refuses, mimicking one that renders audio but rejects metadata.</summary>
        public HashSet<string> Reject { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FakeReceiver()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        private async Task AcceptAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                await using var stream = client.GetStream();
                while (true)
                {
                    var headerText = await ReadHeadersAsync(stream).ConfigureAwait(false);
                    if (headerText is null)
                    {
                        return;
                    }

                    var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in lines.Skip(1))
                    {
                        var colon = line.IndexOf(':');
                        if (colon > 0)
                        {
                            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                        }
                    }

                    var body = Array.Empty<byte>();
                    if (headers.TryGetValue("Content-Length", out var lengthText)
                        && int.TryParse(lengthText, out var length) && length > 0)
                    {
                        body = new byte[length];
                        await stream.ReadExactlyAsync(body).ConfigureAwait(false);
                    }

                    var request = new Request(lines[0].Split(' ')[0], headers, body);
                    lock (Received)
                    {
                        Received.Add(request);
                    }

                    var status = request.ContentType is { } type && Reject.Contains(type)
                        ? "400 Bad Request"
                        : "200 OK";
                    var reply = $"RTSP/1.0 {status}\r\nCSeq: {headers.GetValueOrDefault("CSeq", "1")}\r\nContent-Length: 0\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply)).ConfigureAwait(false);
                }
            }
            catch
            {
                // Test finished and tore the connection down.
            }
        }

        private static async Task<string?> ReadHeadersAsync(Stream stream)
        {
            var buffer = new List<byte>(512);
            var single = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(single).ConfigureAwait(false) == 0)
                {
                    return null;
                }

                buffer.Add(single[0]);
                if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
                {
                    return Encoding.ASCII.GetString(buffer.ToArray());
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }

    private static async Task<List<Request>> SendAsync(TrackMetadata metadata, uint rtpTime = 44100)
    {
        using var receiver = new FakeReceiver();
        using var client = new RtspClient(NullLogger.Instance);
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port, CancellationToken.None);
        client.Uri = "rtsp://test/session";

        await new NowPlaying(client, "Test Speaker", NullLogger.Instance)
            .SendAsync(metadata, rtpTime, CancellationToken.None);

        lock (receiver.Received)
        {
            return [.. receiver.Received];
        }
    }

    private static readonly TrackMetadata FullTrack = new()
    {
        Title = "Blue Monday",
        Artist = "New Order",
        Album = "Power, Corruption & Lies",
        Duration = TimeSpan.FromSeconds(450),
        Position = TimeSpan.FromSeconds(30),
        TrackId = "spotify:track:abc",
        Artwork = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4],
        ArtworkMimeType = "image/jpeg",
    };

    [Fact]
    public async Task SendsProgressThenTrackThenArtwork()
    {
        var requests = await SendAsync(FullTrack);

        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => Assert.Equal("SET_PARAMETER", r.Method));

        // Order is load-bearing: artwork arriving before the listing item has
        // nothing to attach to.
        Assert.Equal("text/parameters", requests[0].ContentType);
        Assert.Equal("application/x-dmap-tagged", requests[1].ContentType);
        Assert.Equal("image/jpeg", requests[2].ContentType);

        Assert.StartsWith("progress: ", requests[0].BodyText);
        Assert.Contains("Blue Monday", requests[1].BodyText);
        Assert.Equal(FullTrack.Artwork, requests[2].Body);
    }

    [Fact]
    public async Task EveryRequestCarriesTheSessionRtpTime()
    {
        var requests = await SendAsync(FullTrack, rtpTime: 123456);

        Assert.All(requests, r => Assert.Equal("rtptime=123456", r.Headers["RTP-Info"]));
    }

    [Fact]
    public async Task ArtworkIsSkippedWhenTheSourceHasNone()
    {
        var requests = await SendAsync(FullTrack with { Artwork = null, ArtworkMimeType = null });

        Assert.Equal(2, requests.Count);
        Assert.DoesNotContain(requests, r => r.ContentType!.StartsWith("image/"));
    }

    [Fact]
    public async Task PngArtworkKeepsItsOwnContentType()
    {
        var requests = await SendAsync(FullTrack with
        {
            Artwork = [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 0, 0],
            ArtworkMimeType = "image/png",
        });

        Assert.Equal("image/png", requests[^1].ContentType);
    }

    [Fact]
    public async Task ProgressAloneIsStillSentWhenNoTrackIsKnown()
    {
        // The buffered anchor pushes whatever it has, even before the source
        // has named a track — a TV needs the progress to start rendering.
        var requests = await SendAsync(new TrackMetadata());

        var request = Assert.Single(requests);
        Assert.Equal("text/parameters", request.ContentType);
        Assert.StartsWith("progress: ", request.BodyText);
    }

    [Fact]
    public async Task ArtworkIsSentOncePerTrackNotOncePerProgressUpdate()
    {
        // Play, pause and seek all re-push progress for the same track; the
        // cover must not go up the control channel again each time.
        using var receiver = new FakeReceiver();
        using var client = new RtspClient(NullLogger.Instance);
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port, CancellationToken.None);
        var sender = new NowPlaying(client, "Test Speaker", NullLogger.Instance);

        await sender.SendAsync(FullTrack, 44100, CancellationToken.None);
        await sender.SendAsync(FullTrack with { Position = TimeSpan.FromSeconds(60) }, 88200, CancellationToken.None);
        await sender.SendAsync(FullTrack with { Position = TimeSpan.FromSeconds(90) }, 132300, CancellationToken.None);

        List<Request> requests;
        lock (receiver.Received)
        {
            requests = [.. receiver.Received];
        }

        Assert.Single(requests.Where(r => r.ContentType!.StartsWith("image/")));
        Assert.Equal(3, requests.Count(r => r.ContentType == "text/parameters"));
    }

    [Fact]
    public async Task ANewTrackGetsItsOwnArtwork()
    {
        using var receiver = new FakeReceiver();
        using var client = new RtspClient(NullLogger.Instance);
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port, CancellationToken.None);
        var sender = new NowPlaying(client, "Test Speaker", NullLogger.Instance);

        await sender.SendAsync(FullTrack, 44100, CancellationToken.None);
        await sender.SendAsync(
            FullTrack with { TrackId = "spotify:track:different", Artwork = [1, 2, 3, 4] },
            88200,
            CancellationToken.None);

        lock (receiver.Received)
        {
            Assert.Equal(2, receiver.Received.Count(r => r.ContentType!.StartsWith("image/")));
        }
    }

    [Fact]
    public async Task ResetMakesTheNextPushResendArtwork()
    {
        // A reconnected receiver has forgotten everything we sent it.
        using var receiver = new FakeReceiver();
        using var client = new RtspClient(NullLogger.Instance);
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port, CancellationToken.None);
        var sender = new NowPlaying(client, "Test Speaker", NullLogger.Instance);

        await sender.SendAsync(FullTrack, 44100, CancellationToken.None);
        sender.Reset();
        await sender.SendAsync(FullTrack, 88200, CancellationToken.None);

        lock (receiver.Received)
        {
            Assert.Equal(2, receiver.Received.Count(r => r.ContentType!.StartsWith("image/")));
        }
    }

    [Fact]
    public async Task ARejectedPartDoesNotStopTheRest()
    {
        // Receivers commonly 4xx the DMAP listing (or the artwork) while
        // rendering audio perfectly. Rejection must not abort the push or
        // throw into the caller — metadata is cosmetic.
        using var receiver = new FakeReceiver();
        receiver.Reject.Add("application/x-dmap-tagged");
        using var client = new RtspClient(NullLogger.Instance);
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port, CancellationToken.None);

        await new NowPlaying(client, "Test Speaker", NullLogger.Instance)
            .SendAsync(FullTrack, 44100, CancellationToken.None);

        lock (receiver.Received)
        {
            Assert.Equal(3, receiver.Received.Count);
            Assert.Equal("image/jpeg", receiver.Received[2].ContentType);
        }
    }
}
