using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.Core.Audio;
using Xunit;

namespace Streamsemble.Wled.Tests;

/// <summary>
/// Pins the lights to the beat: a train of drum hits at arbitrary phases
/// against the hop grid must first light the strip on the hit, in every mode,
/// when windows are scheduled at StartSample + OnsetOffsetSamples.
/// </summary>
public class OnsetTimingTests
{
    private const int Rate = 44100;

    private static (float[] Pcm, List<int> Onsets) KickTrain(int count)
    {
        var rng = new Random(7);
        var onsets = new List<int>();
        var position = Rate;
        for (var i = 0; i < count; i++)
        {
            onsets.Add(position);
            position += (int)(0.7 * Rate) + rng.Next(0, 4096); // never phase-locked to the hop
        }

        var pcm = new float[position + Rate];
        foreach (var onset in onsets)
        {
            for (var n = 0; n < (int)(0.35 * Rate); n++)
            {
                var t = n / (double)Rate;
                pcm[onset + n] = (float)(0.8 * Math.Exp(-t / 0.08)
                    * Math.Sin(2 * Math.PI * (55 * t + 0.6 * (1 - Math.Exp(-t / 0.015)))));
            }
        }

        return (pcm, onsets);
    }

    private static PcmFrame Frame(float[] mono, int start)
    {
        var data = new byte[PcmFrame.CanonicalFrameBytes];
        for (var i = 0; i < PcmFrame.SamplesPerFrame; i++)
        {
            var sample = (short)(mono[start + i] * short.MaxValue);
            data[i * 4] = data[i * 4 + 2] = (byte)sample;
            data[i * 4 + 1] = data[i * 4 + 3] = (byte)(sample >> 8);
        }

        return new PcmFrame(data, start);
    }

    [Theory]
    [InlineData(WledLightMode.Pulse)]
    [InlineData(WledLightMode.Vu)]
    [InlineData(WledLightMode.Spectrum)]
    public void FirstLightLandsOnTheHit(WledLightMode mode)
    {
        var (pcm, onsets) = KickTrain(120);
        var analyzer = new AudioLightAnalyzer();
        var windows = new List<LightWindow>();
        for (var p = 0; p + PcmFrame.SamplesPerFrame <= pcm.Length; p += PcmFrame.SamplesPerFrame)
        {
            windows.AddRange(analyzer.Feed(Frame(pcm, p)));
        }

        var renderer = new WledLightRenderer();
        var settings = new WledRenderSettings((255, 255, 255), WledPalette.Solid, 0f, false, false);
        var shown = windows.Select(w => w.StartSample + AudioLightAnalyzer.OnsetOffsetSamples).ToArray();
        var brightness = windows.Select(w => renderer.Render(mode, w, 60, settings).Sum(b => (int)b)).ToArray();

        // Milliseconds from each hit to the strip reaching 10 % / 50 % of that
        // hit's peak; + = light after sound.
        var firstLight = new List<double>();
        var halfLight = new List<double>();
        foreach (var onset in onsets)
        {
            var from = Array.FindIndex(shown, s => s >= onset - 4096);
            var to = Array.FindIndex(shown, s => s >= onset + (int)(0.3 * Rate));
            var floor = brightness[from];
            var peak = brightness[from..to].Max();
            firstLight.Add((shown[Enumerable.Range(from, to - from).First(i => brightness[i] - floor >= 0.1 * (peak - floor))] - onset) * 1000.0 / Rate);
            halfLight.Add((shown[Enumerable.Range(from, to - from).First(i => brightness[i] - floor >= 0.5 * (peak - floor))] - onset) * 1000.0 / Rate);
        }

        var hopMs = AudioLightAnalyzer.HopSamples * 1000.0 / Rate;
        Assert.InRange(firstLight.Average(), -4.0, 4.0);
        Assert.InRange(halfLight.Average(), -2.0, 10.0);
        // No hit may flash more than a hop early, or trail by more than two.
        Assert.All(firstLight, ms => Assert.InRange(ms, -hopMs, hopMs));
        Assert.All(halfLight, ms => Assert.InRange(ms, -hopMs, 2 * hopMs));
    }

    [Fact]
    public async Task WindowsAnalyzedBeforeTheSpeakersAnchorLandOnTheRealTimeline()
    {
        using var strip = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)strip.Client.LocalEndPoint!).Port;
        using var group = new WledDeviceGroup(
            new WledOptions { Devices = [new WledDeviceOptions { Host = "127.0.0.1", Port = port, Mode = "Pulse" }] },
            NullLogger<WledDeviceGroup>.Instance);

        // Unanchored for the first 100 ms; then the sink reports a timeline on
        // which sample 0 turns audible 600 ms after the audio was ingested —
        // twice as far out as the fallback latency would have guessed.
        var start = Stopwatch.GetTimestamp();
        double Elapsed() => Stopwatch.GetElapsedTime(start).TotalSeconds;
        using var service = new WledLightingService(
            group,
            sample => Elapsed() < 0.1 ? null : 0.6 + sample / (double)Rate - Elapsed(),
            fallbackLatencySeconds: 0.3,
            NullLogger<WledLightingService>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);

        var pcm = new float[PcmFrame.SamplesPerFrame * 8];
        for (var p = 0; p < pcm.Length; p += PcmFrame.SamplesPerFrame)
        {
            service.Ingest(Frame(pcm, p));
        }

        await strip.ReceiveAsync(cts.Token);
        var arrived = Elapsed();
        await service.StopAsync(CancellationToken.None);

        var due = 0.6 + AudioLightAnalyzer.OnsetOffsetSamples / (double)Rate;
        Assert.InRange(arrived, due - 0.005, due + 0.25);
    }
}
