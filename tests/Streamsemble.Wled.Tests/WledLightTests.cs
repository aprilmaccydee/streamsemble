using Streamsemble.Core.Audio;
using Xunit;

namespace Streamsemble.Wled.Tests;

public class WledLightTests
{
    private static LightWindow Window(float level, float[]? bands = null)
        => new(0, level, bands ?? new float[AudioLightAnalyzer.BandCount]);

    private static WledRenderSettings Settings(
        (byte R, byte G, byte B) color = default,
        WledPalette palette = WledPalette.Classic,
        float decay = 0f, bool mirror = false, bool reverse = false)
        => new(color, palette, decay, mirror, reverse);

    private static byte[] Render(WledLightMode mode, in LightWindow window, int ledCount, in WledRenderSettings settings = default)
        => new WledLightRenderer().Render(mode, window, ledCount, settings);

    [Fact]
    public void PulseAtFullLevelShowsTheBaseColor()
    {
        var rgb = Render(WledLightMode.Pulse, Window(1f), 4, Settings((200, 100, 50)));

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(200, rgb[i * 3]);
            Assert.Equal(100, rgb[i * 3 + 1]);
            Assert.Equal(50, rgb[i * 3 + 2]);
        }
    }

    [Fact]
    public void PulseInSilenceIsDark()
        => Assert.All(Render(WledLightMode.Pulse, Window(0f), 4, Settings((200, 100, 50))), b => Assert.Equal(0, b));

    [Fact]
    public void VuFillsFromGreenBottomToRedTop()
    {
        var half = Render(WledLightMode.Vu, Window(0.5f), 10);
        Assert.Equal((byte)0, half[0]);        // first LED pure green
        Assert.Equal((byte)255, half[1]);
        Assert.NotEqual((byte)0, half[4 * 3 + 1]); // 5th LED lit
        Assert.Equal((byte)0, half[5 * 3 + 1]);    // 6th dark
        Assert.Equal((byte)0, half[9 * 3]);        // top dark at half level

        var full = Render(WledLightMode.Vu, Window(1f), 10);
        Assert.Equal((byte)255, full[9 * 3]);      // top LED pure red
        Assert.Equal((byte)0, full[9 * 3 + 1]);
    }

    [Fact]
    public void SpectrumLightsTheRegionOfItsBand()
    {
        var bands = new float[AudioLightAnalyzer.BandCount];
        bands[^1] = 1f; // treble only

        var rgb = Render(WledLightMode.Spectrum, Window(1f, bands), 10);

        Assert.Equal(0, rgb[0] + rgb[1] + rgb[2]);                       // bass end dark
        Assert.True(rgb[9 * 3] + rgb[9 * 3 + 1] + rgb[9 * 3 + 2] > 0);  // treble end lit
    }

    [Fact]
    public void OffRendersBlack()
        => Assert.All(Render(WledLightMode.Off, Window(1f), 4, Settings((255, 255, 255))), b => Assert.Equal(0, b));

    [Fact]
    public void SolidPaletteMetersInTheDeviceColor()
    {
        var rgb = Render(WledLightMode.Vu, Window(1f), 4, Settings((10, 200, 30), WledPalette.Solid));

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(10, rgb[i * 3]);
            Assert.Equal(200, rgb[i * 3 + 1]);
            Assert.Equal(30, rgb[i * 3 + 2]);
        }
    }

    [Fact]
    public void RainbowSpectrumSpansDistinctHues()
    {
        var bands = new float[AudioLightAnalyzer.BandCount];
        Array.Fill(bands, 1f); // broadband: everything lit

        var rgb = Render(WledLightMode.Spectrum, Window(1f, bands), 10, Settings(palette: WledPalette.Rainbow));

        // Every LED lit — and mid-strip differs from the start (the 360° span
        // wraps, so the two ENDS deliberately meet at the same hue).
        for (var i = 0; i < 10; i++)
        {
            Assert.True(rgb[i * 3] + rgb[i * 3 + 1] + rgb[i * 3 + 2] > 0, $"LED {i} dark");
        }

        Assert.NotEqual((rgb[0], rgb[1], rgb[2]), (rgb[12], rgb[13], rgb[14]));
    }

    [Fact]
    public void MirrorGrowsTheMeterFromTheCenter()
    {
        var rgb = Render(WledLightMode.Vu, Window(0.5f), 10, Settings(palette: WledPalette.Solid, color: (255, 255, 255), mirror: true));

        // Half level over a 5-LED virtual half: center pair lit, strip ends dark.
        Assert.True(rgb[4 * 3] > 0);
        Assert.True(rgb[5 * 3] > 0);
        Assert.Equal(0, rgb[0]);
        Assert.Equal(0, rgb[9 * 3]);

        // Reflection is symmetric.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(rgb[(4 - i) * 3], rgb[(5 + i) * 3]);
        }
    }

    [Fact]
    public void ReverseFlipsTheStrip()
    {
        var bands = new float[AudioLightAnalyzer.BandCount];
        bands[^1] = 1f; // treble only

        var rgb = Render(WledLightMode.Spectrum, Window(1f, bands), 10, Settings(reverse: true));

        Assert.True(rgb[0] + rgb[1] + rgb[2] > 0);   // treble now at the start
        Assert.Equal(0, rgb[9 * 3] + rgb[9 * 3 + 1] + rgb[9 * 3 + 2]);
    }

    [Fact]
    public void DecayHoldsTheGlowAfterTheHit()
    {
        var snappy = new WledLightRenderer();
        snappy.Render(WledLightMode.Pulse, Window(1f), 1, Settings((255, 255, 255)));
        var dark = snappy.Render(WledLightMode.Pulse, Window(0f), 1, Settings((255, 255, 255)));
        Assert.Equal(0, dark[0]);

        var silky = new WledLightRenderer();
        silky.Render(WledLightMode.Pulse, Window(1f), 1, Settings((255, 255, 255), decay: 1f));
        var glowing = silky.Render(WledLightMode.Pulse, Window(0f), 1, Settings((255, 255, 255), decay: 1f));
        Assert.True(glowing[0] > 200, $"decay tail too dim: {glowing[0]}");
    }

    [Fact]
    public void PaletteNamesParseCaseInsensitively()
    {
        Assert.Equal(WledPalette.Rainbow, WledPalettes.Parse("rainbow"));
        Assert.Equal(WledPalette.Classic, WledPalettes.Parse("CLASSIC"));
        Assert.Throws<InvalidOperationException>(() => WledPalettes.Parse("disco"));
    }

    [Fact]
    public void ScaleAppliesMasterBrightness()
    {
        var rgb = new byte[] { 200, 100, 0 };
        WledLightRenderer.Scale(rgb, 0.5f);
        Assert.Equal(new byte[] { 100, 50, 0 }, rgb);
    }

    [Fact]
    public void HsvConvertsThePrimaries()
    {
        Assert.Equal(((byte)255, (byte)0, (byte)0), WledLightRenderer.HsvToRgb(0f, 1f, 1f));
        Assert.Equal(((byte)0, (byte)255, (byte)0), WledLightRenderer.HsvToRgb(120f, 1f, 1f));
        Assert.Equal(((byte)0, (byte)0, (byte)255), WledLightRenderer.HsvToRgb(240f, 1f, 1f));
    }

    [Fact]
    public void LightModeNamesParseCaseInsensitively()
    {
        Assert.Equal(WledLightMode.Spectrum, WledLightModes.Parse("spectrum"));
        Assert.Equal(WledLightMode.Off, WledLightModes.Parse("OFF"));
        Assert.Throws<InvalidOperationException>(() => WledLightModes.Parse("disco"));
    }

    [Fact]
    public void ColorParsesHex()
    {
        Assert.Equal(((byte)255, (byte)64, (byte)0), WledDevice.ParseColor("#FF4000"));
        Assert.Equal(((byte)18, (byte)52, (byte)86), WledDevice.ParseColor("123456"));
        Assert.Throws<InvalidOperationException>(() => WledDevice.ParseColor("red"));
    }

    // --- Analyzer ---

    private static PcmFrame SineFrame(long timestamp, double frequencyHz, float amplitude = 0.5f)
    {
        var data = new byte[PcmFrame.CanonicalFrameBytes];
        for (var i = 0; i < PcmFrame.SamplesPerFrame; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * frequencyHz * (timestamp + i) / 44100.0) * amplitude * short.MaxValue);
            data[i * 4] = (byte)sample;
            data[i * 4 + 1] = (byte)(sample >> 8);
            data[i * 4 + 2] = (byte)sample;
            data[i * 4 + 3] = (byte)(sample >> 8);
        }

        return new PcmFrame(data, timestamp);
    }

    [Fact]
    public void SteadySineProducesFullLevelWindowsOnTheHopGrid()
    {
        var analyzer = new AudioLightAnalyzer();
        var windows = new List<LightWindow>();
        for (var ts = 0L; ts < 4400; ts += PcmFrame.SamplesPerFrame)
        {
            windows.AddRange(analyzer.Feed(SineFrame(ts, 440)));
        }

        Assert.True(windows.Count >= 3);
        Assert.Equal(0, windows[0].StartSample);
        Assert.Equal(AudioLightAnalyzer.HopSamples, windows[1].StartSample);
        Assert.All(windows, w => Assert.True(w.Level > 0.9f, $"level {w.Level}"));
    }

    [Fact]
    public void SineEnergyLandsInTheRightBand()
    {
        var analyzer = new AudioLightAnalyzer();
        var windows = new List<LightWindow>();
        for (var ts = 0L; ts < 4400; ts += PcmFrame.SamplesPerFrame)
        {
            windows.AddRange(analyzer.Feed(SineFrame(ts, 440)));
        }

        // 440 Hz on the 43 Hz → 16 kHz log axis lands near band 25 of 64.
        var bands = windows[^1].Bands;
        var peak = Array.IndexOf(bands, bands.Max());
        Assert.InRange(peak, 22, 28);
    }

    [Fact]
    public void TimestampJumpDropsTheStaleTail()
    {
        var analyzer = new AudioLightAnalyzer();
        analyzer.Feed(SineFrame(0, 440));

        var windows = new List<LightWindow>();
        for (var ts = 900_000L; ts < 904_400; ts += PcmFrame.SamplesPerFrame)
        {
            windows.AddRange(analyzer.Feed(SineFrame(ts, 440)));
        }

        Assert.NotEmpty(windows);
        Assert.Equal(900_000, windows[0].StartSample);
    }
}
