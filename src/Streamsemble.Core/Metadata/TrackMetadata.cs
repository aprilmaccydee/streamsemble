namespace Streamsemble.Core.Metadata;

/// <summary>Now-playing metadata, forwarded from the live source to all sinks.</summary>
public sealed record TrackMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public TimeSpan? Duration { get; init; }
    public TimeSpan? Position { get; init; }
    public byte[]? Artwork { get; init; }
    public string? ArtworkMimeType { get; init; }
}
