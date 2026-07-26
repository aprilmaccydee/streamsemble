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
/// Instance state exists for one reason — artwork is by far the largest part
/// and changes only on a track change, while progress updates arrive on every
/// play, pause and seek. Sending the same JPEG again on each of those wastes
/// the control channel and makes some receivers redraw.
/// </summary>
public sealed class NowPlaying(RtspClient rtsp, string displayName, ILogger logger)
{
    private ulong _artworkSentFor;

    /// <summary>Forget what this receiver has been sent — after a reconnect it is a blank slate again.</summary>
    public void Reset() => _artworkSentFor = 0;

    public async Task SendAsync(TrackMetadata metadata, uint rtpTime, CancellationToken ct)
    {
        var rtpInfo = new Dictionary<string, string> { ["RTP-Info"] = $"rtptime={rtpTime}" };

        await SendOneAsync("progress", "text/parameters", Dmap.Progress(metadata, rtpTime), rtpInfo, ct)
            .ConfigureAwait(false);

        if (metadata.HasContent)
        {
            await SendOneAsync("track", Dmap.ContentType, Dmap.TrackItem(metadata), rtpInfo, ct).ConfigureAwait(false);
        }

        if (metadata.Artwork is { Length: > 0 } artwork)
        {
            // Non-zero id keeps "sent" distinguishable from the initial state,
            // so a track whose id happens to hash to 0 still gets its art.
            var artworkId = metadata.PersistentId() | 1UL;
            if (artworkId != _artworkSentFor)
            {
                var mime = string.IsNullOrEmpty(metadata.ArtworkMimeType) ? "image/jpeg" : metadata.ArtworkMimeType;
                await SendOneAsync("artwork", mime, artwork, rtpInfo, ct).ConfigureAwait(false);
                _artworkSentFor = artworkId;
            }
        }

        logger.LogDebug("{Name}: now-playing sent at rtptime {Rtp} ({Title})",
            displayName, rtpTime, metadata.Title ?? "no title");
    }

    private async Task SendOneAsync(
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
    }
}
