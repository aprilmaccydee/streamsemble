using System.Buffers.Binary;
using System.Text;
using Streamsemble.Core.Metadata;

namespace Streamsemble.AirPlay.Sender.Raop;

/// <summary>
/// Minimal DMAP (DAAP tag) writer for AirPlay now-playing metadata. Both RAOP
/// and AirPlay 2 receivers take the same thing: a <c>mlit</c> listing item sent
/// as the body of SET_PARAMETER with content type
/// <c>application/x-dmap-tagged</c>. Tag widths matter — receivers read these
/// by type, not by length, so a duration written as a string is silently
/// dropped (or worse, shifts everything after it).
/// </summary>
public static class Dmap
{
    /// <summary>Content type the DMAP listing item must be sent under.</summary>
    public const string ContentType = "application/x-dmap-tagged";

    /// <summary>
    /// Full now-playing listing item. <c>mper</c> carries a stable per-track id
    /// so a receiver can tell an updated listing from a new track, and can
    /// associate the cover-art push that follows with this item.
    /// </summary>
    public static byte[] TrackItem(TrackMetadata metadata)
    {
        using var payload = new MemoryStream();
        WriteByte(payload, "mikd", 2);                                  // item kind: music
        WriteUInt32(payload, "miid", (uint)(metadata.PersistentId() & 0xFFFFFFFF));
        WriteUInt64(payload, "mper", metadata.PersistentId());
        WriteString(payload, "minm", metadata.Title);
        WriteString(payload, "asar", metadata.Artist);
        WriteString(payload, "asal", metadata.Album);
        WriteString(payload, "asaa", metadata.AlbumArtist);
        if (metadata.Duration is { } duration)
        {
            WriteUInt32(payload, "astm", (uint)Math.Clamp(duration.TotalMilliseconds, 0, uint.MaxValue));
        }

        if (metadata.TrackNumber is { } track)
        {
            WriteUInt16(payload, "astn", (ushort)Math.Clamp(track, 0, ushort.MaxValue));
        }

        if (metadata.DiscNumber is { } disc)
        {
            WriteUInt16(payload, "asdn", (ushort)Math.Clamp(disc, 0, ushort.MaxValue));
        }

        return Tag("mlit", payload.ToArray());
    }

    /// <summary>
    /// The <c>progress: start/current/end</c> body (SET_PARAMETER,
    /// <c>text/parameters</c>) that drives a receiver's scrub bar. All three are
    /// RTP timestamps on the session's own timeline at 44.1 kHz.
    ///
    /// A live stream has no natural end, so when the source reports a duration
    /// and a position we place <paramref name="currentRtp"/> inside a window of
    /// that shape; without them we fall back to a long window, which reads as
    /// "playing, position unknown" rather than a bar pinned at either end.
    /// </summary>
    public static byte[] Progress(TrackMetadata metadata, uint currentRtp, int sampleRate = 44100)
    {
        uint start;
        uint end;
        if (metadata.Duration is { TotalSeconds: > 0 } duration)
        {
            var positionSamples = (long)((metadata.Position?.TotalSeconds ?? 0) * sampleRate);
            var durationSamples = (long)(duration.TotalSeconds * sampleRate);
            // Unchecked: RTP time is a free-running u32 and wraps by design;
            // the receiver does the same arithmetic on its side.
            start = unchecked((uint)(currentRtp - positionSamples));
            end = unchecked((uint)(start + durationSamples));
        }
        else
        {
            start = unchecked(currentRtp - (uint)sampleRate);
            end = unchecked(start + (uint)sampleRate * 3600);
        }

        return Encoding.ASCII.GetBytes($"progress: {start}/{currentRtp}/{end}\r\n");
    }

    private static void WriteString(Stream stream, string code, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            stream.Write(Tag(code, Encoding.UTF8.GetBytes(value)));
        }
    }

    private static void WriteByte(Stream stream, string code, byte value) => stream.Write(Tag(code, [value]));

    private static void WriteUInt16(Stream stream, string code, ushort value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(Tag(code, buffer));
    }

    private static void WriteUInt32(Stream stream, string code, uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(Tag(code, buffer));
    }

    private static void WriteUInt64(Stream stream, string code, ulong value)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(Tag(code, buffer));
    }

    private static byte[] Tag(string code, byte[] payload)
    {
        var result = new byte[8 + payload.Length];
        Encoding.ASCII.GetBytes(code, result);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(4), payload.Length);
        payload.CopyTo(result, 8);
        return result;
    }
}
