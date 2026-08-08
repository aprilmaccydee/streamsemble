using System.Buffers.Binary;
using System.Numerics;

namespace Streamsemble.AirPlay.Common.FairPlay;

/// <summary>
/// Which message permutation a compression applies after round 31. The three
/// call sites in the protocol each use a different one, which is why this
/// cannot be a single "the" modified MD5.
/// </summary>
internal enum FairPlayMd5Mutation
{
    /// <summary>Pairwise swaps driven by the working state's low nibbles.</summary>
    Swap,

    /// <summary>The same eight positions, rotated by one instead of swapped.</summary>
    Cycle,

    /// <summary>Two swaps plus three more taken from higher nibbles; used by the key KDF.</summary>
    Kdf,
}

/// <summary>
/// MD5's compression function with two deliberate deviations: message words are
/// read big-endian rather than little-endian, and the message schedule is
/// permuted in place once, immediately after round 31. Neither can be expressed
/// through a stock MD5 implementation, which is why this exists rather than
/// calling <c>System.Security.Cryptography.MD5</c>.
///
/// This is a compression function, not a hash: no padding and no length
/// appending happen here. Callers assemble their own padded material — the
/// protocol's records are fixed-size and pad themselves.
/// </summary>
internal static class FairPlayMd5
{
    internal const int BlockSize = 64;

    internal static uint[] Compress(uint[] state, ReadOnlySpan<byte> block, FairPlayMd5Mutation mutation)
    {
        var message = new uint[16];
        for (var i = 0; i < message.Length; i++)
        {
            message[i] = BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
        }

        uint a = state[0], b = state[1], c = state[2], d = state[3];
        for (var round = 0; round < 64; round++)
        {
            uint mix;
            int word;
            if (round < 16)
            {
                mix = (b & c) | (~b & d);
                word = round;
            }
            else if (round < 32)
            {
                mix = (d & b) | (~d & c);
                word = (5 * round + 1) & 15;
            }
            else if (round < 48)
            {
                mix = b ^ c ^ d;
                word = (3 * round + 5) & 15;
            }
            else
            {
                mix = c ^ (b | ~d);
                word = 7 * round & 15;
            }

            var rotated = BitOperations.RotateLeft(
                unchecked(a + mix + FairPlayTables.Md5Constant[round] + message[word]),
                FairPlayTables.Md5Shift[round]);
            (a, b, c, d) = (d, unchecked(b + rotated), b, c);

            if (round == 31)
            {
                Mutate(message, a, b, c, d, mutation);
            }
        }

        return [unchecked(state[0] + a), unchecked(state[1] + b), unchecked(state[2] + c), unchecked(state[3] + d)];
    }

    private static void Mutate(uint[] message, uint a, uint b, uint c, uint d, FairPlayMd5Mutation mutation)
    {
        void Swap(int i, int j) => (message[i], message[j]) = (message[j], message[i]);

        if (mutation == FairPlayMd5Mutation.Kdf)
        {
            Swap((int)(a & 15), (int)(b & 15));
            Swap((int)(c & 15), (int)(d & 15));
            for (var shift = 4; shift <= 12; shift += 4)
            {
                Swap((int)((a >> shift) & 15), (int)((b >> shift) & 15));
            }

            return;
        }

        int[] positions =
        [
            (int)(a & 15), (int)(b & 15), (int)(c & 15), (int)(d & 15),
            (int)((a >> 4) & 15), (int)((b >> 4) & 15), (int)((c >> 4) & 15), (int)((d >> 4) & 15),
        ];

        if (mutation == FairPlayMd5Mutation.Swap)
        {
            for (var i = 0; i < positions.Length; i++)
            {
                Swap(i, positions[i]);
            }

            return;
        }

        // Cycle: the values at those eight positions each move back one slot.
        var first = message[positions[0]];
        for (var i = 0; i < positions.Length - 1; i++)
        {
            message[positions[i]] = message[positions[i + 1]];
        }

        message[positions[^1]] = first;
    }

    /// <summary>Reads a 16-byte seed as four little-endian words — the state's input form.</summary>
    internal static uint[] WordsFromLittleEndian(ReadOnlySpan<byte> seed)
    {
        var words = new uint[4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(seed[(i * 4)..]);
        }

        return words;
    }

    /// <summary>Writes four words big-endian — the state's output form. The asymmetry is the protocol's, not ours.</summary>
    internal static byte[] WordsToBigEndian(uint[] words)
    {
        var bytes = new byte[16];
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), words[i]);
        }

        return bytes;
    }
}
