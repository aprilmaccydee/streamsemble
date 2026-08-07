namespace Streamsemble.Wled;

/// <summary>Per-device render tunables, snapshotted once per light frame.</summary>
/// <param name="Color">Base color: Pulse's glow, and everything under the Solid palette.</param>
/// <param name="Palette">Color treatment for the mode.</param>
/// <param name="DecaySeconds">Fall half-life in seconds (0–1): 0 snaps with the
/// analyzer's own envelope, 1 trails a long tail behind transients.</param>
/// <param name="Mirror">Render from the strip's center outward (mirrored halves).</param>
/// <param name="Reverse">Flip the strip end-for-end; with Mirror, the show
/// grows from the ends inward instead.</param>
public readonly record struct WledRenderSettings(
    (byte R, byte G, byte B) Color,
    WledPalette Palette,
    float DecaySeconds,
    bool Mirror,
    bool Reverse);

/// <summary>
/// Maps analysis windows onto one strip's RGB pixels. Holds that strip's
/// visual state (fall envelopes, rainbow phase) — one instance per device,
/// only ever touched from the lighting service's send loop.
/// </summary>
public sealed class WledLightRenderer
{
    private const float HopSeconds = (float)AudioLightAnalyzer.HopSamples / 44100f;
    private const float RainbowDriftDegreesPerSecond = 18f;

    private readonly float[] _bandEnv = new float[AudioLightAnalyzer.BandCount];
    private float _levelEnv;
    private float _huePhase;

    public byte[] Render(WledLightMode mode, in LightWindow window, int ledCount, in WledRenderSettings settings)
    {
        _huePhase = (_huePhase + RainbowDriftDegreesPerSecond * HopSeconds) % 360f;

        var fall = settings.DecaySeconds <= 0f
            ? 0f
            : (float)Math.Pow(0.5, HopSeconds / settings.DecaySeconds);

        var rgb = new byte[ledCount * 3];
        // Mirror renders a half-length virtual strip, reflected outward from
        // the center after the mode fills it.
        var pixels = settings.Mirror ? (ledCount + 1) / 2 : ledCount;

        switch (mode)
        {
            case WledLightMode.Pulse:
                RenderPulse(Fall(ref _levelEnv, window.Level, fall), settings, rgb);
                break;
            case WledLightMode.Vu:
                RenderVu(Fall(ref _levelEnv, window.Level, fall), pixels, settings, rgb);
                break;
            case WledLightMode.Spectrum:
                var count = Math.Min(_bandEnv.Length, window.Bands.Length);
                for (var b = 0; b < count; b++)
                {
                    Fall(ref _bandEnv[b], window.Bands[b], fall);
                }

                RenderSpectrum(_bandEnv, pixels, settings, rgb);
                break;
        }

        if (settings.Mirror)
        {
            MirrorOut(rgb, ledCount, pixels);
        }

        if (settings.Reverse)
        {
            ReverseStrip(rgb);
        }

        return rgb; // Off renders black — used to blank a strip on release
    }

    private static float Fall(ref float envelope, float value, float fall)
        => envelope = Math.Max(value, envelope * fall);

    private void RenderPulse(float level, in WledRenderSettings settings, byte[] rgb)
    {
        // Square for perceptual brightness — LEDs are savagely nonlinear.
        var brightness = level * level;
        var (cr, cg, cb) = settings.Palette == WledPalette.Rainbow
            ? HsvToRgb(_huePhase, 1f, 1f)
            : settings.Color;
        for (var i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = (byte)(cr * brightness);
            rgb[i + 1] = (byte)(cg * brightness);
            rgb[i + 2] = (byte)(cb * brightness);
        }
    }

    private void RenderVu(float level, int pixels, in WledRenderSettings settings, byte[] rgb)
    {
        var lit = level * pixels;
        for (var i = 0; i < pixels; i++)
        {
            // Full LEDs at full brightness, the meter's tip fades in — the
            // fractional remainder keeps the needle moving between LEDs.
            var intensity = Math.Clamp(lit - i, 0f, 1f);
            if (intensity <= 0)
            {
                break;
            }

            var position = pixels > 1 ? (float)i / (pixels - 1) : 1f;
            var (r, g, b) = settings.Palette switch
            {
                WledPalette.Solid => ((byte)(settings.Color.R * intensity),
                                      (byte)(settings.Color.G * intensity),
                                      (byte)(settings.Color.B * intensity)),
                WledPalette.Rainbow => HsvToRgb(position * 300f + _huePhase, 1f, intensity),
                // Green over the first 60 %, then through amber to red at the top.
                _ => HsvToRgb(120f * (1f - Math.Clamp((position - 0.6f) / 0.4f, 0f, 1f)), 1f, intensity),
            };
            rgb[i * 3] = r;
            rgb[i * 3 + 1] = g;
            rgb[i * 3 + 2] = b;
        }
    }

    private void RenderSpectrum(float[] bands, int pixels, in WledRenderSettings settings, byte[] rgb)
    {
        for (var i = 0; i < pixels; i++)
        {
            var position = pixels > 1 ? (float)i / (pixels - 1) : 0f;
            var band = position * (bands.Length - 1);
            var low = (int)band;
            var high = Math.Min(low + 1, bands.Length - 1);
            var value = bands[low] + (bands[high] - bands[low]) * (band - low);
            var v = value * value;

            var (r, g, b) = settings.Palette switch
            {
                WledPalette.Solid => ((byte)(settings.Color.R * v),
                                      (byte)(settings.Color.G * v),
                                      (byte)(settings.Color.B * v)),
                WledPalette.Rainbow => HsvToRgb(position * 360f + _huePhase, 1f, v),
                // Bass red at the strip's start through to violet treble.
                _ => HsvToRgb(position * 280f, 1f, v),
            };
            rgb[i * 3] = r;
            rgb[i * 3 + 1] = g;
            rgb[i * 3 + 2] = b;
        }
    }

    private static void MirrorOut(byte[] rgb, int ledCount, int half)
    {
        var virtualStrip = new byte[half * 3];
        Array.Copy(rgb, virtualStrip, virtualStrip.Length);

        var center = ledCount / 2;
        var odd = (ledCount & 1) == 1;
        for (var i = 0; i < half; i++)
        {
            var right = center + i;
            var left = odd ? center - i : center - 1 - i;
            for (var c = 0; c < 3; c++)
            {
                rgb[right * 3 + c] = virtualStrip[i * 3 + c];
                rgb[left * 3 + c] = virtualStrip[i * 3 + c];
            }
        }
    }

    private static void ReverseStrip(byte[] rgb)
    {
        for (int a = 0, b = rgb.Length - 3; a < b; a += 3, b -= 3)
        {
            for (var c = 0; c < 3; c++)
            {
                (rgb[a + c], rgb[b + c]) = (rgb[b + c], rgb[a + c]);
            }
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
