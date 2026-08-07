using Streamsemble.Core.Audio;

namespace Streamsemble.Wled;

/// <summary>
/// One analysis window's worth of light-relevant signal.
/// </summary>
/// <param name="StartSample">Capture-counter timestamp of the window's first
/// sample — the key the sink's timeline maps to an audible instant.</param>
/// <param name="Level">Loudness envelope, auto-gained to 0..1.</param>
/// <param name="Bands">Log-spaced spectrum, auto-gained to 0..1 per band.</param>
public readonly record struct LightWindow(long StartSample, float Level, float[] Bands);

/// <summary>
/// Turns canonical PCM into ~43 light windows per second: a loudness envelope
/// and a log-spaced spectrum, both normalized by slow-decaying auto gain so
/// they use the full range at any source volume — no sensitivity knob.
/// Single-threaded: feed it only from the lighting service's loop.
/// </summary>
public sealed class AudioLightAnalyzer
{
    public const int WindowSamples = 2048; // ~46 ms at 44.1 kHz → 21.5 Hz bins
    public const int HopSamples = 1024;    // ~43 windows/s drive the strips
    public const int BandCount = 64;

    private const float LowHz = 43f;      // first usable FFT bin
    private const float HighHz = 16000f;
    private const float ReferenceFloor = 0.02f;  // ≈ −34 dBFS: quiet rooms stay dark instead of flaring noise
    private const float ReferenceDecay = 0.9990f; // per window ≈ 20 s half-life for the auto-gain peak
    private const float EnvelopeDecay = 0.86f;    // per window ≈ fast fall, instant attack

    private readonly float[] _window = new float[WindowSamples];
    private readonly float[] _hann = new float[WindowSamples];
    private readonly float[] _fftRe = new float[WindowSamples];
    private readonly float[] _fftIm = new float[WindowSamples];
    private readonly int[] _bandOfBin = new int[WindowSamples / 2];
    private readonly float[] _bandEnv = new float[BandCount];
    private readonly float[] _bandScratch = new float[BandCount];
    private readonly int[] _bandBinCounts = new int[BandCount];

    private int _filled;
    private long _windowStartSample;
    private long _expectedNextSample;
    private bool _tracking;
    private float _levelEnv;
    private float _levelRef = ReferenceFloor;
    private float _bandRef = ReferenceFloor;

    public AudioLightAnalyzer()
    {
        for (var i = 0; i < WindowSamples; i++)
        {
            _hann[i] = 0.5f - 0.5f * (float)Math.Cos(2.0 * Math.PI * i / WindowSamples);
        }

        // Bin → log-spaced band map, precomputed once.
        for (var bin = 0; bin < WindowSamples / 2; bin++)
        {
            var hz = bin * 44100f / WindowSamples;
            var band = hz <= LowHz
                ? 0
                : (int)(Math.Log(hz / LowHz) / Math.Log(HighHz / LowHz) * BandCount);
            _bandOfBin[bin] = Math.Min(band, BandCount - 1);
        }
    }

    /// <summary>Forget everything buffered (source switch, seek, pause).</summary>
    public void Reset()
    {
        _filled = 0;
        _tracking = false;
        _levelEnv = 0;
        Array.Clear(_bandEnv);
    }

    private readonly List<LightWindow> _emitted = [];

    /// <summary>Feeds one pipeline frame; returns a window per hop crossed.
    /// The list is reused — consume it before the next Feed.</summary>
    public IReadOnlyList<LightWindow> Feed(PcmFrame frame)
    {
        _emitted.Clear();

        // A timestamp jump means the buffered tail belongs to other audio.
        if (_tracking && frame.Timestamp != _expectedNextSample)
        {
            Reset();
        }

        if (!_tracking)
        {
            _tracking = true;
            _windowStartSample = frame.Timestamp;
        }

        _expectedNextSample = frame.Timestamp + frame.SampleCount;

        var data = frame.Data.Span;
        for (var i = 0; i + 3 < data.Length; i += 4)
        {
            var left = (short)(data[i] | (data[i + 1] << 8));
            var right = (short)(data[i + 2] | (data[i + 3] << 8));
            _window[_filled++] = (left + right) / 65536f;

            if (_filled == WindowSamples)
            {
                _emitted.Add(Analyze());
                Array.Copy(_window, HopSamples, _window, 0, WindowSamples - HopSamples);
                _filled = WindowSamples - HopSamples;
                _windowStartSample += HopSamples;
            }
        }

        return _emitted;
    }

    private LightWindow Analyze()
    {
        // Loudness: RMS → instant-attack, fast-fall envelope → auto gain.
        var sumSquares = 0f;
        for (var i = 0; i < WindowSamples; i++)
        {
            sumSquares += _window[i] * _window[i];
        }

        var rms = (float)Math.Sqrt(sumSquares / WindowSamples);
        _levelEnv = Math.Max(rms, _levelEnv * EnvelopeDecay);
        _levelRef = Math.Max(Math.Max(rms, ReferenceFloor), _levelRef * ReferenceDecay);
        var level = Math.Min(1f, _levelEnv / _levelRef);

        // Spectrum: Hann window → FFT → log-spaced band magnitudes.
        for (var i = 0; i < WindowSamples; i++)
        {
            _fftRe[i] = _window[i] * _hann[i];
            _fftIm[i] = 0;
        }

        Fft.Transform(_fftRe, _fftIm);

        Array.Clear(_bandScratch);
        Array.Clear(_bandBinCounts);
        for (var bin = 1; bin < WindowSamples / 2; bin++)
        {
            var power = _fftRe[bin] * _fftRe[bin] + _fftIm[bin] * _fftIm[bin];
            _bandScratch[_bandOfBin[bin]] += power;
            _bandBinCounts[_bandOfBin[bin]]++;
        }

        var bandPeak = 0f;
        for (var b = 0; b < BandCount; b++)
        {
            var mean = _bandBinCounts[b] > 0 ? _bandScratch[b] / _bandBinCounts[b] : 0f;
            var magnitude = (float)Math.Sqrt(mean);
            _bandEnv[b] = Math.Max(magnitude, _bandEnv[b] * EnvelopeDecay);
            bandPeak = Math.Max(bandPeak, _bandEnv[b]);
        }

        _bandRef = Math.Max(Math.Max(bandPeak, ReferenceFloor / 4), _bandRef * ReferenceDecay);
        var bands = new float[BandCount];
        for (var b = 0; b < BandCount; b++)
        {
            bands[b] = Math.Min(1f, _bandEnv[b] / _bandRef);
        }

        return new LightWindow(_windowStartSample, level, bands);
    }
}
