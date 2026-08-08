namespace Streamsemble.AirPlay.Common.FairPlay;

/// <summary>
/// FairPlay's SAP hash: a 64-byte block in, 16 bytes out. It is not a
/// cryptographic hash and has no structure worth reasoning about — it is a
/// fixed pile of byte arithmetic over four state arrays, and the only property
/// that matters is that it produces exactly the bytes the other end expects.
///
/// Everything here is deliberately literal. The statement order is load-bearing
/// (later lines read what earlier lines wrote, including through index
/// indirections), so there is nothing to simplify and every apparent redundancy
/// is real. All arithmetic wraps at eight bits except where a value is widened
/// on purpose before a divide or an index — those places are marked.
/// </summary>
internal static class FairPlaySapHash
{
    internal const int BlockSize = 64;
    private const int DigestSize = 16;
    private const int WorkSize = 210;

    internal static byte[] Compute(ReadOnlySpan<byte> block)
    {
        if (block.Length < BlockSize)
        {
            throw new ArgumentException($"SAP hash consumes {BlockSize}-byte blocks, got {block.Length}", nameof(block));
        }

        var hash = (byte[])FairPlayTables.SapInitialHash.Clone();
        var matrix = (byte[])FairPlayTables.SapInitialMatrix.Clone();
        var aux = new byte[10];
        var work = new byte[WorkSize];

        // The block is loaded in reversed four-byte groups and repeats to fill
        // the work array, which is not a multiple of the block size.
        for (var i = 0; i < work.Length; i++)
        {
            work[i] = block[(i & 63) ^ 3];
        }

        // Four scramble passes over the ring. The cursor is unsigned and the
        // back-references are subtractions, so the first pass reads through
        // indices produced by 32-bit wraparound rather than from the ring's
        // tail — that difference is part of the function, not a bug to fix.
        for (uint i = 0; i < 840; i++)
        {
            var x = work[(i - 155) % WorkSize];
            var y = work[(i - 57) % WorkSize];
            var z = work[(i - 13) % WorkSize];
            var w = work[i % WorkSize];
            work[i % WorkSize] = (byte)(Rotl(y, 5) + (Rotl(z, 3) ^ w) - Rotl(x, 7));
        }

        NonlinearCircuit(hash, matrix, aux, work);

        var digest = new byte[DigestSize];
        aux.AsSpan(0, 3).CopyTo(digest);
        aux.AsSpan(3, 7).CopyTo(digest.AsSpan(4));
        for (var i = 0; i < digest.Length; i++)
        {
            digest[i] += 0xE1;
        }

        // Two lanes are overwritten wholesale after the bias is applied, so
        // they carry constants rather than state.
        digest[3] = 0x3D;
        digest[11] = 0x3C;
        digest[10] ^= (byte)(aux[3] ^ 133);

        // Fold the whole work array down into the 16 lanes, mixing the hash and
        // matrix arrays into the lanes they line up with.
        for (var i = 0; i < work.Length; i++)
        {
            var value = work[i];
            if (i < matrix.Length)
            {
                value ^= matrix[i];
            }

            if (i < hash.Length)
            {
                value ^= hash[i];
            }

            digest[i & 15] ^= value;
        }

        // Reverse scramble across the lanes.
        for (var i = 0; i < 256; i++)
        {
            digest[i & 15] ^= (byte)(Rotl(digest[(i - 7) & 15], 1)
                                     ^ Rotl(digest[(i - 5) & 15], 6)
                                     ^ Rotl(digest[(i - 1) & 15], 5));
        }

        return digest;
    }

    private static byte Rotl(byte value, int count)
    {
        count &= 7;
        return (byte)((value << count) | (value >> (8 - count)));
    }

    /// <summary>Rotate, except that a zero rotation yields zero rather than the input.</summary>
    private static byte RotateOrZero(byte value, byte count) => count == 0 ? (byte)0 : Rotl(value, count);

    /// <summary>
    /// Seed lookup whose index is computed WIDE: the shift is applied to a
    /// promoted integer, so the index routinely exceeds 255 before the modulo.
    /// Truncating it to a byte first silently changes the result.
    /// </summary>
    private static byte WideSeed(byte value, byte count) => count == 0
        ? FairPlayTables.SapSeed[0]
        : FairPlayTables.SapSeed[((value << count) | (value >> (8 - count))) % FairPlayTables.SapSeed.Length];

    private static byte Majority(byte a, byte b, byte c) => (byte)(a ^ ((a ^ b) & (a ^ c)));

    private static byte SelectBits(byte mask, byte ifSet, byte ifClear) => (byte)(ifClear ^ ((ifSet ^ ifClear) & mask));

    private static byte Square(byte value) => (byte)(value * value);

    private static byte Cube(byte value) => (byte)(value * value * value);

    private static void NonlinearCircuit(byte[] hash, byte[] matrix, byte[] aux, byte[] work)
    {
        // The circuit reads its inputs through several layers of indirection.
        // These close over the live arrays, so each call sees whatever the
        // preceding statements wrote — which is the whole point.
        byte Hi(byte i) => hash[i % 20];
        byte Si(byte i) => FairPlayTables.SapSeed[i % 21];
        byte H(int i) => Hi(work[i]);
        byte M(int i) => matrix[work[i] % 35];
        byte S(int i) => Si(work[i]);
        byte Ma(int i) => matrix[aux[i] % 35];

        matrix[12] = (byte)(0x14 + (SelectBits(92, work[64], (byte)(work[99] / 3)) & WideSeed(S(206), 4)));
        work[4] = (byte)(2 * Square((byte)(work[99] / 5)));
        work[153] ^= (byte)(Square(M(203)) * work[190]);
        hash[3] = (byte)(0x13 ^ ((S(205) >> 1) & 0x10));
        work[33] -= (byte)(S(36) & ~9);
        aux[5] = (byte)(((M(67) & ~2) | 1 | ((H(181) >> 6) & 2) | (hash[3] & 0x10)) - 15);
        matrix[12] = 0x07;
        work[2] -= 64;
        hash[19] = S(58);
        aux[4] = (byte)(92 - M(32));
        aux[9] = (byte)(M(15) + 0x9E);
        work[34] += (byte)(Si(aux[9]) / 5);
        hash[19] += (byte)(0xE6 ^ ((Hi(aux[9]) >> 1) & 0x66));
        work[15] ^= (byte)(3 * RotateOrZero(work[72], (byte)(-S(190) & 7)) - 9 * S(126));
        hash[15] ^= Cube(M(181));
        matrix[4] ^= (byte)(work[202] / 3);
        matrix[1] += Cube(Majority((byte)(92 - Hi(aux[4])), (byte)~work[105], 0xC6));
        hash[19] ^= (byte)((224 | (S(92) & 27)) * M(41) / 3);
        work[140] += RotateOrZero(92, (byte)(-work[5] & 7));
        matrix[12] += Majority((byte)(~work[4] ^ M(12)), work[182], 192);
        work[36] += 125;
        work[124] = Rotl(Majority(Majority(work[138], hash[15], 74), H(43), 95), 4);

        var auxHash = Hi(aux[9]);
        aux[1] = (byte)(0x4C & ~(auxHash & (byte)(S(68) << 1)));
        aux[2] = (byte)(222 - Majority(
            (byte)((work[177] + S(79)) >> 1),
            (byte)(3 * work[148] / 5),
            matrix[1]));
        matrix[16] += (byte)(((Ma(4) & ~0x60) | auxHash | 8) - (Rotl(work[33], 2) | 128));
        hash[14] ^= Ma(2);
        work[19] += Majority(
            RotateOrZero(Si(H(201)), (byte)((byte)(M(112) << 1) & 6)),
            (byte)(((H(208) & ~0x7C) | (H(164) & 0x7C)) / 5),
            37);
        matrix[8] = (byte)(RotateOrZero(140, (byte)(-Square(S(45)) & 7)) ^ aux[4]);
        work[190] = 56;
        work[53] = (byte)~((H(83) | 204) / 5);
        hash[13] += H(41);
        hash[10] = (byte)(Majority(Ma(4), work[2], aux[2]) / 15);
        aux[3] = (byte)(92 - Square((byte)(0x28 | (Ma(1) & (0x12 | (S(2) & 4))))));

        var seedBits = Si(aux[4]);
        matrix[13] ^= seedBits;
        aux[6] = (byte)(92 + Square(Majority((byte)(M(179) - 38), aux[2], 177)));

        var expansionBits = Majority((byte)(aux[3] + (aux[4] & 74)), (byte)~seedBits, 121);
        work[47] ^= (byte)(M(89) + Majority((byte)(expansionBits ^ 0xA6), aux[4], 4));
        aux[7] = (byte)(seedBits / 3 - Ma(9)
                        - (0x14 | (work[151] & ((aux[4] & 0x88) | 0x62)) | (aux[4] & 0x22)));

        var expandedSelector = (byte)(expansionBits ^ ((aux[4] & 0xCA) >> 1) ^ 75);
        aux[9] += (byte)(0x80 | (Majority(aux[7], work[151], 0x20) & 0x64) | (seedBits & 0x44) | (Ma(9) & 0x1B));
        matrix[33] ^= work[26];
        matrix[30] = (byte)((aux[9] / 3 - ((aux[4] & ~8) | 0x13)) ^ H(122));
        work[22] = (byte)((M(90) & 0x1B) | 0x44);

        // Widened on purpose: the cube overflows a byte before the shift.
        var wide = (int)SelectBits(71, matrix[expandedSelector % 35], Si(aux[5]));
        matrix[18] += (byte)(wide * wide * wide >> 1);
        matrix[5] -= S(92);
        matrix[18] ^= (byte)(SelectBits(aux[3], Ma(3), SelectBits(16, M(183), work[41]))
                             * SelectBits(expandedSelector, H(59), work[17]));
        matrix[22] = (byte)(Majority(
            SelectBits((byte)(hash[14] | 28), (byte)((work[7] & 28) | 0x82), H(93)),
            RotateOrZero(Ma(4), (byte)(RotateOrZero(work[11], (byte)(-M(28) & 7)) & 7)),
            matrix[33]) + 74);
        hash[15] -= Majority(Majority(aux[3], aux[4], 214), Si((byte)(H(39) ^ 217)), aux[6]);

        var hash9 = Hi(aux[9]);
        var indexedHash = Hi((byte)((byte)(aux[4] / 3 - (aux[9] | work[22]))
                                    ^ aux[6]
                                    ^ (((M(57) | hash9) & (0x52 | (aux[9] & 0x0D)))
                                       | (((M(57) & hash9) | aux[9]) & 0x20))));
        aux[6] = (byte)(Square(Square(H(99))) | Ma(9));
        aux[1] += (byte)(RotateOrZero((byte)(H(151) | S(202)), (byte)(H(50) & 7))
                         + Majority(
                             H(4),
                             (byte)((SelectBits(matrix[16], indexedHash, M(138))
                                     + SelectBits(17, work[33], S(39))) / 5),
                             147));
        aux[0] = SelectBits(
            (byte)(hash[10] & 7),
            (byte)(Ma(6) & H(209)),
            SelectBits(0x47, RotateOrZero(S(127), (byte)(Ma(6) & 7)), (byte)(Si(Ma(5)) << 1)));

        var selectedSquare = SelectBits(198, Square(M(14)), (byte)(H(145) ^ aux[0]));
        var seed9 = Si(aux[9]);
        var hash3 = Hi(aux[3]);
        matrix[2] += (byte)(((byte)(hash3 << 1) & ((work[25] & 0x96) | (seed9 & 8))) | (seed9 & 0x40));
        matrix[14] -= SelectBits(34, work[97], (byte)(Ma(3) & (aux[0] ^ M(100))));
        work[23] ^= (byte)(Majority(Majority(S(17), hash3, aux[0]), (byte)(work[50] / 3), 0x76) << 1);
        hash[17] = 115;
        hash[13] = (byte)(((Majority(Hi(aux[7]), work[10], 82) >> 1) & 0x68) | (H(39) & 0x17));
        matrix[33] -= (byte)(work[113] & 9);
        matrix[28] -= (byte)((aux[3] & ~0x20) | ((work[110] >> 1) & 0x20));
        work[95] = Si(aux[3]);
        hash[15] = (byte)(Majority((byte)(work[95] - 48), (byte)~work[184], 189)
                          & Cube(Majority(aux[7], Si(aux[1]), 0xAA)));
        matrix[22] += work[183];
        aux[4] ^= (byte)(3 * S(1));
        aux[5] += (byte)(198 * Majority(S(178), Ma(1), 209) * H(13) * (S(26) >> 1));
        aux[8] = SelectBits(10, Ma(3), Ma(9));
        matrix[18] -= SelectBits(hash[15], (byte)(aux[5] / 15), Cube((byte)(Hi(aux[6]) | 81)));
        aux[1] += (byte)(Si(Hi(aux[1])) / 3 - H(160));
        hash[16] = (byte)(147 - Majority(
            aux[0],
            Majority(S(69), work[172], (byte)(aux[2] - selectedSquare + 77)),
            (byte)(0xC2 | (aux[0] & 5))));
        hash[3] -= WideSeed(Majority(S(155), work[105], 141), (byte)(Majority(S(168), H(29), 6) & 7));
        work[5] = (byte)(RotateOrZero(0x38, (byte)(-(H(61) / 5) & 7)) ^ (byte)~Ma(8) / 5);
        work[198] += work[3];

        wide = 162 | Ma(9);
        work[164] += (byte)(wide * wide / 5);
        aux[2] = (byte)(Majority(RotateOrZero(139, (byte)(-aux[5] & 6)), Hi(aux[3]), 12)
                        | SelectBits(95, Cube(seed9), Hi(aux[7])));
        matrix[12] += (byte)((16 | ((work[103] | 60) & (aux[2] | (work[103] & 32)))) / 3);
        work[143] -= (byte)(0x12 | (SelectBits(
                                        aux[9],
                                        SelectBits(matrix[8], work[35], aux[7]),
                                        (byte)(aux[8] / 3))
                                    & (0x4D | ((work[172] >> 1) & 0x20))));
        matrix[29] = 162;
        hash[15] += Majority(
            (byte)(M(149) ^ Square(work[43])),
            (byte)(SelectBits(95, H(125), Si(aux[1])) >> 1),
            115);
        aux[9] -= Hi(aux[7]);
        hash[7] -= Square(RotateOrZero(Ma(5), (byte)(-M(17) * (M(17) & 1))));
        matrix[8] += (byte)(Cube(S(202)) - work[184]);
        hash[16] = (byte)((byte)(M(102) << 1) & 0x84);
        aux[6] ^= (byte)(Si(aux[7]) >> 1);
        hash[7] -= (byte)(H(191) - SelectBits(177, Si(Si(aux[1])), (byte)(S(80) << 1)));
        hash[6] = H(119);
        hash[12] = (byte)((Hi(aux[8]) ^ (byte)(M(71) + M(15)))
                          & Majority((byte)((work[118] & ~0x2C) | 2), Square(Hi(aux[9])), 27));

        var digestIndex = (byte)(SelectBits(0xA9, (byte)(S(57) * 231), Majority(work[32], Ma(1), 23)) / 5);
        var seedSample = Si(aux[6]);
        aux[5] = (byte)(Majority(
            (byte)((seedSample & 0x1C) | (H(82) & 0xA2) | (Si(digestIndex) & 0x41)),
            Majority(Cube(Hi(aux[7])), work[82], 92),
            192) ^ digestIndex);
        matrix[25] ^= (byte)(2 * Hi(aux[9]) * work[5]
                             - (RotateOrZero(aux[4], (byte)(seedSample & 7)) & (byte)(aux[3] + 110)));
    }
}
