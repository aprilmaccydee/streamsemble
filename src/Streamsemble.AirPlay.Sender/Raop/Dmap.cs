using System.Buffers.Binary;
using System.Text;
using Streamsemble.Core.Metadata;

namespace Streamsemble.AirPlay.Sender.Raop;

/// <summary>Minimal DMAP (DAAP tag) writer for RAOP now-playing metadata.</summary>
public static class Dmap
{
    public static byte[] TrackItem(TrackMetadata metadata)
    {
        using var payload = new MemoryStream();
        WriteString(payload, "minm", metadata.Title);
        WriteString(payload, "asar", metadata.Artist);
        WriteString(payload, "asal", metadata.Album);
        return Tag("mlit", payload.ToArray());
    }

    private static void WriteString(Stream stream, string code, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            stream.Write(Tag(code, Encoding.UTF8.GetBytes(value)));
        }
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
