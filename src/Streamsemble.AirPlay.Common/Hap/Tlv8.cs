namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// TLV8 codec for HAP pairing bodies: a sequence of (1-byte type, 1-byte
/// length, value) items. A value longer than 255 bytes is split across
/// consecutive items of the same type, each ≤ 255 bytes; on decode, adjacent
/// same-type items concatenate — but a run of exactly-255-byte items followed
/// by nothing more of that type is still one value. Distinct logical items of
/// the same type are separated by a zero-length <see cref="TlvType.Separator"/>.
/// </summary>
public sealed class Tlv8
{
    private readonly List<(byte Type, byte[] Value)> _items = [];

    public Tlv8 Add(TlvType type, byte[] value)
    {
        _items.Add(((byte)type, value));
        return this;
    }

    public Tlv8 Add(TlvType type, byte value) => Add(type, [value]);

    public Tlv8 AddState(byte state) => Add(TlvType.State, state);

    public Tlv8 AddSeparator()
    {
        _items.Add(((byte)TlvType.Separator, []));
        return this;
    }

    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        foreach (var (type, value) in _items)
        {
            if (value.Length == 0)
            {
                stream.WriteByte(type);
                stream.WriteByte(0);
                continue;
            }

            var offset = 0;
            while (offset < value.Length)
            {
                var chunk = Math.Min(255, value.Length - offset);
                stream.WriteByte(type);
                stream.WriteByte((byte)chunk);
                stream.Write(value, offset, chunk);
                offset += chunk;
            }
        }

        return stream.ToArray();
    }

    /// <summary>Decodes to a map of type → concatenated value (first logical item per type wins on separators).</summary>
    public static IReadOnlyDictionary<byte, byte[]> Decode(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<byte, byte[]>();
        var buffers = new Dictionary<byte, MemoryStream>();
        var lastType = -1;
        var offset = 0;

        while (offset + 2 <= data.Length)
        {
            var type = data[offset];
            var length = data[offset + 1];
            offset += 2;
            if (offset + length > data.Length)
            {
                break;
            }

            var value = data.Slice(offset, length);
            offset += length;

            if (type == (byte)TlvType.Separator)
            {
                lastType = -1;
                continue;
            }

            // Fragment continuation only when the SAME type repeats immediately
            // and the previous fragment was a full 255 bytes.
            var buffer = buffers.TryGetValue(type, out var existing) ? existing : null;
            if (buffer is not null && lastType == type)
            {
                buffer.Write(value);
            }
            else if (!result.ContainsKey(type))
            {
                buffer = new MemoryStream();
                buffer.Write(value);
                buffers[type] = buffer;
            }

            lastType = type;
        }

        foreach (var (type, buffer) in buffers)
        {
            result[type] = buffer.ToArray();
        }

        return result;
    }
}

public static class TlvDictionaryExtensions
{
    public static byte[]? Get(this IReadOnlyDictionary<byte, byte[]> tlv, TlvType type)
        => tlv.TryGetValue((byte)type, out var value) ? value : null;

    public static byte? GetByte(this IReadOnlyDictionary<byte, byte[]> tlv, TlvType type)
        => tlv.Get(type) is { Length: > 0 } value ? value[0] : null;
}
