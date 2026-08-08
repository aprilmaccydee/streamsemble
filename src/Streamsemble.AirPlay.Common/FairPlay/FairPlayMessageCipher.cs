namespace Streamsemble.AirPlay.Common.FairPlay;

/// <summary>
/// The block cipher protecting the 128-byte SAP value inside an FPLY message.
///
/// It is AES's round structure — SubBytes, ShiftRows, MixColumns, AddRoundKey,
/// ten rounds — with the key schedule replaced by four hard-coded sets of
/// middle round keys, one per protocol mode, sharing a single first and last
/// round key. There is no key: the mode byte in the message selects which set
/// of constants to run. That is why the platform's AES cannot be used here and
/// the rounds are spelled out.
///
/// The eight blocks chain as CBC from a per-mode fixed IV.
/// </summary>
internal static class FairPlayMessageCipher
{
    internal const int SapLength = 128;
    private const int BlockLength = 16;
    private const int BlockCount = SapLength / BlockLength;

    /// <summary>Offset of the encrypted SAP body inside an m3-shaped record.</summary>
    internal const int BodyOffset = 16;

    /// <summary>Offset of the mode byte inside an m3-shaped record.</summary>
    internal const int ModeOffset = 12;

    /// <summary>
    /// Decrypts the SAP body of an m3-shaped record (mode at byte 12, body at
    /// bytes 16..144) into <paramref name="plaintext"/>.
    /// </summary>
    internal static void DecryptMessage(ReadOnlySpan<byte> message, Span<byte> plaintext)
    {
        var mode = message[ModeOffset];
        RequireKnownMode(mode);

        Span<byte> state = stackalloc byte[BlockLength];
        for (var step = 0; step < BlockCount; step++)
        {
            // Mode 3 walks the chain backwards. For separate buffers the result
            // is identical either way; doing it in this order additionally lets
            // a caller decrypt a record in place.
            var index = mode == 3 ? BlockCount - 1 - step : step;
            var start = BodyOffset + index * BlockLength;

            message.Slice(start, BlockLength).CopyTo(state);
            DecryptBlock(state, mode);

            var chain = index > 0
                ? message.Slice(start - BlockLength, BlockLength)
                : FairPlayTables.MessageIvs.AsSpan(mode * BlockLength, BlockLength);
            for (var i = 0; i < BlockLength; i++)
            {
                plaintext[index * BlockLength + i] = (byte)(state[i] ^ chain[i]);
            }
        }
    }

    /// <summary>Encrypts a 128-byte SAP value into the body an m3 carries.</summary>
    internal static void EncryptMessage(byte mode, ReadOnlySpan<byte> plaintext, Span<byte> encrypted)
    {
        RequireKnownMode(mode);
        if (plaintext.Length != SapLength || encrypted.Length != SapLength)
        {
            throw new ArgumentException(
                $"FairPlay message bodies are {SapLength} bytes, got {plaintext.Length} and {encrypted.Length}");
        }

        var chain = FairPlayTables.MessageIvs.AsSpan(mode * BlockLength, BlockLength);
        Span<byte> state = stackalloc byte[BlockLength];
        for (var block = 0; block < BlockCount; block++)
        {
            var start = block * BlockLength;
            for (var i = 0; i < BlockLength; i++)
            {
                state[i] = (byte)(plaintext[start + i] ^ chain[i]);
            }

            EncryptBlock(state, mode);
            state.CopyTo(encrypted[start..]);
            chain = encrypted.Slice(start, BlockLength);
        }
    }

    private static void RequireKnownMode(byte mode)
    {
        if (mode >= FairPlayTables.ModeCount)
        {
            throw new InvalidDataException($"unsupported FairPlay message mode {mode}");
        }
    }

    private static ReadOnlySpan<byte> MiddleKey(byte mode, int round) =>
        FairPlayTables.MessageMiddleKeys.AsSpan((mode * 9 + round) * BlockLength, BlockLength);

    private static void DecryptBlock(Span<byte> state, byte mode)
    {
        Xor(state, FairPlayTables.MessageRoundKey10);
        for (var round = 9; round > 0; round--)
        {
            InverseShiftRows(state);
            Substitute(state, FairPlayTables.InverseSBox);
            Xor(state, MiddleKey(mode, round - 1));
            InverseMixColumns(state);
        }

        InverseShiftRows(state);
        Substitute(state, FairPlayTables.InverseSBox);
        Xor(state, FairPlayTables.MessageRoundKey0);
    }

    private static void EncryptBlock(Span<byte> state, byte mode)
    {
        Xor(state, FairPlayTables.MessageRoundKey0);
        Substitute(state, FairPlayTables.ForwardSBox);
        ShiftRows(state);
        for (var round = 0; round < 9; round++)
        {
            MixColumns(state);
            Xor(state, MiddleKey(mode, round));
            Substitute(state, FairPlayTables.ForwardSBox);
            ShiftRows(state);
        }

        Xor(state, FairPlayTables.MessageRoundKey10);
    }

    private static void Xor(Span<byte> state, ReadOnlySpan<byte> key)
    {
        for (var i = 0; i < state.Length; i++)
        {
            state[i] ^= key[i];
        }
    }

    private static void Substitute(Span<byte> state, byte[] box)
    {
        for (var i = 0; i < state.Length; i++)
        {
            state[i] = box[state[i]];
        }
    }

    private static void ShiftRows(Span<byte> state)
    {
        Span<byte> previous = stackalloc byte[BlockLength];
        state.CopyTo(previous);
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                state[4 * column + row] = previous[4 * ((column + row) & 3) + row];
            }
        }
    }

    private static void InverseShiftRows(Span<byte> state)
    {
        Span<byte> previous = stackalloc byte[BlockLength];
        state.CopyTo(previous);
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                state[4 * column + row] = previous[4 * ((column - row + 4) & 3) + row];
            }
        }
    }

    private static void MixColumns(Span<byte> state)
    {
        for (var column = 0; column < 4; column++)
        {
            var offset = column * 4;
            byte a = state[offset], b = state[offset + 1], c = state[offset + 2], d = state[offset + 3];
            state[offset] = (byte)(Multiply(a, 2) ^ Multiply(b, 3) ^ c ^ d);
            state[offset + 1] = (byte)(a ^ Multiply(b, 2) ^ Multiply(c, 3) ^ d);
            state[offset + 2] = (byte)(a ^ b ^ Multiply(c, 2) ^ Multiply(d, 3));
            state[offset + 3] = (byte)(Multiply(a, 3) ^ b ^ c ^ Multiply(d, 2));
        }
    }

    private static void InverseMixColumns(Span<byte> state)
    {
        for (var column = 0; column < 4; column++)
        {
            var offset = column * 4;
            byte a = state[offset], b = state[offset + 1], c = state[offset + 2], d = state[offset + 3];
            state[offset] = (byte)(Multiply(a, 14) ^ Multiply(b, 11) ^ Multiply(c, 13) ^ Multiply(d, 9));
            state[offset + 1] = (byte)(Multiply(a, 9) ^ Multiply(b, 14) ^ Multiply(c, 11) ^ Multiply(d, 13));
            state[offset + 2] = (byte)(Multiply(a, 13) ^ Multiply(b, 9) ^ Multiply(c, 14) ^ Multiply(d, 11));
            state[offset + 3] = (byte)(Multiply(a, 11) ^ Multiply(b, 13) ^ Multiply(c, 9) ^ Multiply(d, 14));
        }
    }

    /// <summary>Carry-less multiply in GF(2^8) modulo AES's x^8 + x^4 + x^3 + x + 1.</summary>
    private static byte Multiply(byte a, byte b)
    {
        byte product = 0;
        while (b != 0)
        {
            if ((b & 1) != 0)
            {
                product ^= a;
            }

            var high = a & 0x80;
            a <<= 1;
            if (high != 0)
            {
                a ^= 0x1B;
            }

            b >>= 1;
        }

        return product;
    }
}
