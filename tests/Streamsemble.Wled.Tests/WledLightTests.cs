using Streamsemble.Core.Audio;
using Xunit;

namespace Streamsemble.Wled.Tests;

public class WledLightTests
{
    private static LightWindow Window(float level, float[]? bands = null)
        => new(0, level, bands ?? new float[AudioLightAnalyzer.BandCount]);

    [Fact]
    public void PulseAtFullLevelShowsTheBaseColor()
    {
        var rgb = WledLightRenderer.Render(WledLightMode.Pulse, Window(1f), 4, (200, 100, 50));

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(200, rgb[i * 3]);
            Assert.Equal(100, rgb[i * 3 + 1]);
            Assert.Equal(50, rgb[i * 3 + 2]);
        }
    }

    [Fact]
    public void PulseInSilenceIsDark()
        => Assert.All(WledLightRenderer.Render(WledLightMode.Pulse, Window(0f), 4, (200, 100, 50)), b => Assert.Equal(0, b));

    [Fact]
    public void VuFillsFromGreenBottomToRedTop()
    {
        var half = WledLightRenderer.Render(WledLightMode.Vu, Window(0.5f), 10, default);
        Assert.Equal((byte)0, half[0]);        // first LED pure green
        Assert.Equal((byte)255, half[1]);
        Assert.NotEqual((byte)0, half[4 * 3 + 1]); // 5th LED lit
        Assert.Equal((byte)0, half[5 * 3 + 1]);    // 6th dark
        Assert.Equal((byte)0, half[9 * 3]);        // top dark at half level

        var full = WledLightRenderer.Render(WledLightMode.Vu, Window(1f), 10, default);
        Assert.Equal((byte)255, full[9 * 3]);      // top LED pure red
        Assert.Equal((byte)0, full[9 * 3 + 1]);
    }

    [Fact]
    public void SpectrumLightsTheRegionOfItsBand()
    {
        var bands = new float[AudioLightAnalyzer.BandCount];
        bands[^1] = 1f; // treble only

        var rgb = WledLightRenderer.Render(WledLightMode.Spectrum, Window(1f, bands), 10, default);

        Assert.Equal(0, rgb[0] + rgb[1] + rgb[2]);                       // bass end dark
        Assert.True(rgb[9 * 3] + rgb[9 * 3 + 1] + rgb[9 * 3 + 2] > 0);  // treble end lit
    }

    [Fact]
    public void OffRendersBlack()
        => Assert.All(WledLightRenderer.Render(WledLightMode.Off, Window(1f), 4, (255, 255, 255)), b => Assert.Equal(0, b));

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
