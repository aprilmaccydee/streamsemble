namespace Streamsemble.Core.Metadata;

/// <summary>Now-playing metadata, forwarded from the live source to all sinks.</summary>
public sealed record TrackMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }

    /// <summary>Album artist when it differs from the track artist (compilations, features).</summary>
    public string? AlbumArtist { get; init; }

    public TimeSpan? Duration { get; init; }
    public TimeSpan? Position { get; init; }

    /// <summary>Track number within its disc; 0/null when the source doesn't say.</summary>
    public int? TrackNumber { get; init; }

    public int? DiscNumber { get; init; }

    /// <summary>
    /// Source-native identity (Spotify URI, etc). Receivers key their artwork
    /// cache off the DMAP persistent id we derive from this, so a stable value
    /// per track matters more than its format.
    /// </summary>
    public string? TrackId { get; init; }

    public byte[]? Artwork { get; init; }
    public string? ArtworkMimeType { get; init; }

    /// <summary>Where <see cref="Artwork"/> came from — kept for the web UI and for debugging fetch failures.</summary>
    public string? ArtworkUrl { get; init; }

    /// <summary>True once there is something worth pushing to a receiver.</summary>
    public bool HasContent =>
        !string.IsNullOrEmpty(Title) || !string.IsNullOrEmpty(Artist) || !string.IsNullOrEmpty(Album);

    /// <summary>
    /// Stable 64-bit identity for the DMAP <c>mper</c> tag: receivers use it to
    /// tell "new track" from "same track, updated progress", and to associate a
    /// cover-art push with the listing item it belongs to.
    /// </summary>
    public ulong PersistentId()
    {
        if (string.IsNullOrEmpty(TrackId) && !HasContent)
        {
            return 0;
        }

        // Separated so "Live|Aid" and "Live Aid|" can't collide into one id.
        var seed = TrackId ?? $"{Title}|{Artist}|{Album}";

        // FNV-1a 64: no crypto need, just a deterministic spread that stays the
        // same across reconnects so a re-sent listing item keeps its identity.
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(seed))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }
}
