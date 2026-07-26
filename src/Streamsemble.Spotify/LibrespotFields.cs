using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Streamsemble.Spotify.Tests")]

namespace Streamsemble.Spotify;

/// <summary>
/// Interpreting the environment variables librespot hands its --onevent
/// script. Pure text munging, kept apart from the process supervision so the
/// awkward parts (which cover is the big one, how a multi-artist list reads on
/// a speaker display) can be tested directly.
/// </summary>
internal static class LibrespotFields
{
    /// <summary>Newline-separated librespot list (artists, album artists) → one display line.</summary>
    public static string? JoinList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : string.Join(", ", value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Pick the biggest cover librespot offered. COVERS is newline-separated
    /// and NOT size-ordered, but Spotify's CDN encodes the size class in the
    /// image id prefix, so rank by that and fall back to librespot's order for
    /// anything unrecognised (a local file, or a CDN scheme change). Getting
    /// this wrong is not fatal, just ugly: a 64×64 thumbnail stretched across
    /// a TV's now-playing screen.
    /// </summary>
    public static string? PickCover(string? covers)
    {
        if (string.IsNullOrWhiteSpace(covers))
        {
            return null;
        }

        return covers.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((url, index) => (url, index))
            .OrderByDescending(x => CoverRank(x.url))
            .ThenBy(x => x.index)
            .Select(x => x.url)
            .FirstOrDefault();
    }

    private static int CoverRank(string url) => url switch
    {
        _ when url.Contains("ab67616d0000b273", StringComparison.Ordinal) => 3,   // 640×640
        _ when url.Contains("ab67616d00001e02", StringComparison.Ordinal) => 2,   // 300×300
        _ when url.Contains("ab67616d00004851", StringComparison.Ordinal) => 1,   // 64×64
        _ => 0,
    };

    /// <summary>Content-Type is usually present and correct; this is the fallback when it isn't.</summary>
    public static string SniffImageMime(byte[] bytes) =>
        bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G'
            ? "image/png"
            : "image/jpeg";
}
