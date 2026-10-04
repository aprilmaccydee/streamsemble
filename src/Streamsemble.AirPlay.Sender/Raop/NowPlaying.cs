using Microsoft.Extensions.Logging;
using Streamsemble.Core.Metadata;

namespace Streamsemble.AirPlay.Sender.Raop;

/// <summary>
/// Pushes now-playing metadata over one session's RTSP control channel. The
/// wire format is the same for classic RAOP and for HAP-paired AirPlay 2 — the
/// AirPlay 2 requests just ride the encrypted stream — so both session types
/// own one of these.
///
/// Up to three SET_PARAMETER requests, in this order, each tagged with the
/// session's current RTP time so the receiver can line the update up with the
/// audio it is about to render:
///   1. <c>text/parameters</c> — <c>progress: start/current/end</c>
///   2. <c>application/x-dmap-tagged</c> — the track listing item
///   3. <c>image/jpeg</c> (or png) — cover art, keyed to the listing item's
///      persistent id by arriving immediately after it
/// Order matters: a receiver that gets artwork before a listing item has
/// nothing to attach it to and drops it.
///
/// Artwork is by far the largest part, while progress updates arrive on every
/// play, pause and seek. Suppress unchanged artwork after a successful send,
/// but allow a cover arriving after its track listing to replace an earlier
/// image. Keep each listing/artwork batch together even when updates overlap.
/// </summary>
public sealed class NowPlaying(RtspClient rtsp, string displayName, ILogger logger)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ArtworkKey? _artworkSent;
    private int _resetVersion;

    private sealed record ArtworkKey(int ResetVersion, ulong TrackId, string Version);

    /// <summary>Forget what this receiver has been sent — after a reconnect it is a blank slate again.</summary>
    public void Reset() => Interlocked.Increment(ref _resetVersion);

    public async Task SendAsync(TrackMetadata metadata, uint rtpTime, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A reset during an in-flight send must not let that send populate
            // the cache for the receiver's new session.
            var resetVersion = Volatile.Read(ref _resetVersion);
            var rtpInfo = new Dictionary<string, string> { ["RTP-Info"] = $"rtptime={rtpTime}" };

            await SendOneAsync("progress", "text/parameters", Dmap.Progress(metadata, rtpTime), rtpInfo, ct)
                .ConfigureAwait(false);

            if (metadata.HasContent)
            {
                await SendOneAsync("track", Dmap.ContentType, Dmap.TrackItem(metadata), rtpInfo, ct).ConfigureAwait(false);
            }

            if (metadata.Artwork is { Length: > 0 } artwork)
            {
                var artworkKey = new ArtworkKey(resetVersion, metadata.PersistentId(), metadata.ArtworkVersion()!);
                if (artworkKey != _artworkSent)
                {
                    var mime = string.IsNullOrEmpty(metadata.ArtworkMimeType) ? "image/jpeg" : metadata.ArtworkMimeType;
                    if (await SendOneAsync("artwork", mime, artwork, rtpInfo, ct).ConfigureAwait(false))
                    {
                        _artworkSent = artworkKey;
                    }
                }
            }

            logger.LogDebug("{Name}: now-playing sent at rtptime {Rtp} ({Title})",
                displayName, rtpTime, metadata.Title ?? "no title");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<bool> SendOneAsync(
        string what,
        string contentType,
        byte[] body,
        Dictionary<string, string> headers,
        CancellationToken ct)
    {
        var response = await rtsp.RequestAsync("SET_PARAMETER", ct, contentType, body, headers).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            // Metadata is cosmetic: plenty of receivers 4xx one part (commonly
            // artwork) and render audio perfectly. Never fail a stream over it.
            logger.LogDebug("{Name}: {What} metadata rejected ({Status} {Reason})",
                displayName, what, response.StatusCode, response.ReasonPhrase);
        }

        return response.IsSuccess;
    }
}
