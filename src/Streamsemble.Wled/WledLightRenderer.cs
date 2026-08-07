namespace Streamsemble.Wled;

/// <summary>Maps one analysis window onto a strip's RGB pixels for a light mode.</summary>
public static class WledLightRenderer
{
    public static byte[] Render(WledLightMode mode, in LightWindow window, int ledCount, (byte R, byte G, byte B) color)
    {
        var rgb = new byte[ledCount * 3];
        switch (mode)
        {
            case WledLightMode.Pulse:
                RenderPulse(window.Level, color, rgb);
                break;
            case WledLightMode.Vu:
                RenderVu(window.Level, ledCount, rgb);
                break;
            case WledLightMode.Spectrum:
                RenderSpectrum(window.Bands, ledCount, rgb);
                break;
        }

        return rgb; // Off renders black — used to blank a strip on release
    }

    private static void RenderPulse(float level, (byte R, byte G, byte B) color, byte[] rgb)
    {
        // Square for perceptual brightness — LEDs are savagely nonlinear.
        var brightness = level * level;
        for (var i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = (byte)(color.R * brightness);
            rgb[i + 1] = (byte)(color.G * brightness);
            rgb[i + 2] = (byte)(color.B * brightness);
        }
    }

    private static void RenderVu(float level, int ledCount, byte[] rgb)
    {
        var lit = level * ledCount;
        for (var i = 0; i < ledCount; i++)
        {
            // Full LEDs at full brightness, the meter's tip fades in — the
            // fractional remainder keeps the needle moving between LEDs.
            var intensity = Math.Clamp(lit - i, 0f, 1f);
            if (intensity <= 0)
            {
                break;
            }

            // Green over the first 60 %, then through amber to red at the top.
            var position = ledCount > 1 ? (float)i / (ledCount - 1) : 1f;
            var hue = 120f * (1f - Math.Clamp((position - 0.6f) / 0.4f, 0f, 1f));
            var (r, g, b) = HsvToRgb(hue, 1f, intensity);
            rgb[i * 3] = r;
            rgb[i * 3 + 1] = g;
            rgb[i * 3 + 2] = b;
        }
    }

    private static void RenderSpectrum(float[] bands, int ledCount, byte[] rgb)
    {
        for (var i = 0; i < ledCount; i++)
        {
            var position = ledCount > 1 ? (float)i / (ledCount - 1) : 0f;
            var band = position * (bands.Length - 1);
            var low = (int)band;
            var high = Math.Min(low + 1, bands.Length - 1);
            var value = bands[low] + (bands[high] - bands[low]) * (band - low);

            // Bass red at the strip's start through to violet treble.
            var (r, g, b) = HsvToRgb(position * 280f, 1f, value * value);
            rgb[i * 3] = r;
            rgb[i * 3 + 1] = g;
            rgb[i * 3 + 2] = b;
        }
    }

    /// <summary>Applies a device's master brightness to a rendered frame.</summary>
    public static void Scale(byte[] rgb, float brightness)
    {
        if (brightness >= 1f)
        {
            return;
        }

        for (var i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)(rgb[i] * brightness);
        }
    }

    public static (byte R, byte G, byte B) HsvToRgb(float hueDegrees, float saturation, float value)
    {
        var h = ((hueDegrees % 360f) + 360f) % 360f / 60f;
        var c = value * saturation;
        var x = c * (1f - Math.Abs(h % 2f - 1f));
        var m = value - c;
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
