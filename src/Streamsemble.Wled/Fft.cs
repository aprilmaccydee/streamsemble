namespace Streamsemble.Wled;

/// <summary>Iterative in-place radix-2 complex FFT — all the lighting analyzer needs.</summary>
internal static class Fft
{
    public static void Transform(float[] re, float[] im)
    {
        var n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("FFT length must be a power of two", nameof(re));
        }

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j |= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2.0 * Math.PI / len;
            var wRe = (float)Math.Cos(angle);
            var wIm = (float)Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                var curRe = 1f;
                var curIm = 0f;
                for (var k = 0; k < len / 2; k++)
                {
                    var evenRe = re[i + k];
                    var evenIm = im[i + k];
                    var oddRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
                    var oddIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;
                    re[i + k] = evenRe + oddRe;
                    im[i + k] = evenIm + oddIm;
                    re[i + k + len / 2] = evenRe - oddRe;
                    im[i + k + len / 2] = evenIm - oddIm;
                    (curRe, curIm) = (curRe * wRe - curIm * wIm, curRe * wIm + curIm * wRe);
                }
            }
        }
    }
}
