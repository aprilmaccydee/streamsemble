using System.Diagnostics;

namespace Streamsemble.Core.Audio;

/// <summary>
/// Test source: 200 ms bursts of 440 Hz sine once per second (a click track),
/// generated at realtime rate. Used to exercise the pipeline and the AirPlay
/// sender end-to-end without a real remote app, and to measure inter-speaker
/// alignment by cross-correlating what each speaker played (enable with
/// Streamsemble:TestTone in config).
/// </summary>
public sealed class ToneSource() : AudioSourceBase("TestTone")
{
    private CancellationTokenSource? _cts;

    public override Task StopAsync(CancellationToken cancellationToken = default)
    {
        SetState(Core.Abstractions.SourceState.Idle);
        return Task.CompletedTask;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        SetState(Core.Abstractions.SourceState.Active);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        const double frequency = 440.0;
        const double amplitude = 0.2;
        // Continuous sine instead of the click track — buffered AAC receivers
        // choke on the degenerate ~6-byte frames the encoder emits for pure
        // digital silence, so debugging needs a gap-free signal.
        var continuous = Environment.GetEnvironmentVariable("STREAMSEMBLE_TONE_CONTINUOUS") == "1";
        // Click period in seconds (default 1s). Longer periods disambiguate
        // larger inter-speaker offsets when measuring alignment by mic.
        var periodSeconds = double.TryParse(
            Environment.GetEnvironmentVariable("STREAMSEMBLE_TONE_PERIOD"), out var p) && p > 0.25 ? p : 1.0;
        var periodSamples = (long)(periodSeconds * 44100);
        // Real buffered senders outrun realtime and burst seconds of audio into
        // the receiver instantly; raise the lookahead to reproduce that.
        var lookaheadSeconds = double.TryParse(
            Environment.GetEnvironmentVariable("STREAMSEMBLE_TONE_LOOKAHEAD"), out var la) ? la : 0.5;
        // Chirp bursts (300→3000 Hz linear sweep) instead of a pure tone: a
        // chirp autocorrelates to a single sharp peak, so mic recordings can be
        // aligned by cross-correlation to ~1 ms — a pure tone is ambiguous to
        // its own period. Used for inter-speaker calibration.
        var chirp = Environment.GetEnvironmentVariable("STREAMSEMBLE_TONE_CHIRP") == "1";
        const int burstSamples = 44100 / 5; // 200 ms
        var format = AudioFormat.Canonical;
        var stopwatch = Stopwatch.StartNew();
        long generatedSamples = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            // Stay at most `lookaheadSeconds` ahead of real time.
            var realTimeSamples = (long)(stopwatch.Elapsed.TotalSeconds * format.SampleRate);
            if (generatedSamples > realTimeSamples + (long)(format.SampleRate * lookaheadSeconds))
            {
                await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                continue;
            }

            var buffer = new byte[PcmFrame.CanonicalFrameBytes];
            for (var i = 0; i < PcmFrame.SamplesPerFrame; i++)
            {
                var sample = generatedSamples + i;
                var posInPeriod = sample % periodSamples;
                var inBurst = continuous || posInPeriod < burstSamples;
                short value = 0;
                if (inBurst)
                {
                    if (chirp && !continuous)
                    {
                        // Linear 300→3000 Hz sweep across the burst; phase is the
                        // integral of the instantaneous frequency.
                        var t = (double)posInPeriod / format.SampleRate;
                        var phase = 2 * Math.PI * (300 * t + (3000 - 300) / (2 * 0.2) * t * t);
                        value = (short)(Math.Sin(phase) * amplitude * short.MaxValue);
                    }
                    else
                    {
                        value = (short)(Math.Sin(2 * Math.PI * frequency * sample / format.SampleRate) * amplitude * short.MaxValue);
                    }
                }
                buffer[i * 4] = (byte)value;
                buffer[i * 4 + 1] = (byte)(value >> 8);
                buffer[i * 4 + 2] = (byte)value;
                buffer[i * 4 + 3] = (byte)(value >> 8);
            }

            EmitPcm(buffer);
            generatedSamples += PcmFrame.SamplesPerFrame;
        }
    }
}
