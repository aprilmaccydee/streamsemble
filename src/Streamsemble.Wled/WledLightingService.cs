using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Streamsemble.Core.Audio;

namespace Streamsemble.Wled;

/// <summary>
/// Drives the WLED strips from the music. The tap sink feeds it the exact
/// frames the speakers were sent; each analysis window is rendered per device
/// and HELD until the moment that audio turns audible — the sink's
/// capture→audible mapping, the same timeline the speakers anchor on — so the
/// lights land on the beat the listener hears, not the beat the pump saw a
/// group latency earlier.
/// </summary>
public sealed class WledLightingService : BackgroundService
{
    private readonly WledDeviceGroup _devices;
    private readonly Func<long, double?> _secondsUntilAudible;
    private readonly double _fallbackLatencySeconds;
    private readonly ILogger<WledLightingService> _logger;

    // ~1.5 s of frames / ~6 s of scheduled windows; DropOldest keeps a stalled
    // consumer from ever back-pressuring the audio path.
    private readonly Channel<PcmFrame> _frames = Channel.CreateBounded<PcmFrame>(
        new BoundedChannelOptions(192) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly Channel<Scheduled> _pending = Channel.CreateBounded<Scheduled>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

    private readonly AudioLightAnalyzer _analyzer = new();
    private int _epoch;
    private int _resetPending;
    private long _lateDropped;

    // Where in a window a fresh onset actually sits. Half-overlapping windows
    // mean a window's first half was already the previous window's second
    // half, so the first window to contain an onset always holds it
    // Hop..Window-1 samples in — expected position (Window+Hop)/2. Scheduling
    // on StartSample made every flash lead the beat by that much (~23–46 ms);
    // scheduling on the expected onset centers the residual to ±Hop/2.
    private const int OnsetCenterSamples = (AudioLightAnalyzer.WindowSamples + AudioLightAnalyzer.HopSamples) / 2;

    private sealed record Scheduled(int Epoch, long DueStopwatchTimestamp, LightWindow Window);

    public WledLightingService(
        WledDeviceGroup devices,
        Func<long, double?> secondsUntilAudible,
        double fallbackLatencySeconds,
        ILogger<WledLightingService> logger)
    {
        _devices = devices;
        _secondsUntilAudible = secondsUntilAudible;
        _fallbackLatencySeconds = fallbackLatencySeconds;
        _logger = logger;
    }

    /// <summary>Tap-sink hook: one pipeline frame on its way to the speakers.</summary>
    public void Ingest(PcmFrame frame) => _frames.Writer.TryWrite(frame);

    /// <summary>Tap-sink hook mirroring the sink's flush semantics: a skip's
    /// queued light frames belong to abandoned audio either way; a pause
    /// additionally hands the strips back to their own idle effect.</summary>
    public void NotifyFlush(bool dropQueuedAudio)
    {
        Interlocked.Increment(ref _epoch);
        Interlocked.Exchange(ref _resetPending, 1);
        if (!dropQueuedAudio)
        {
            _ = ReleaseAllAsync();
        }
    }

    public void NotifyStreamStop()
    {
        Interlocked.Increment(ref _epoch);
        Interlocked.Exchange(ref _resetPending, 1);
        _ = ReleaseAllAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_devices.IsConfigured)
        {
            return;
        }

        _logger.LogInformation(
            "lighting engine driving {Count} WLED device(s), scheduled on the audible timeline",
            _devices.Devices.Count);
        await Task.WhenAll(
            Task.Run(() => AnalyzeLoopAsync(stoppingToken), stoppingToken),
            Task.Run(() => SendLoopAsync(stoppingToken), stoppingToken)).ConfigureAwait(false);
    }

    private async Task AnalyzeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref _resetPending, 0) == 1)
                {
                    _analyzer.Reset();
                }

                var epoch = Volatile.Read(ref _epoch);
                foreach (var window in _analyzer.Feed(frame))
                {
                    // The sink says when this window's audio turns audible;
                    // convert to a local stopwatch deadline the send loop can
                    // sleep toward. Unanchored (nothing playing yet, or a
                    // WAV/null sink) falls back to the configured latency.
                    var wait = _secondsUntilAudible(window.StartSample + OnsetCenterSamples) ?? _fallbackLatencySeconds;
                    var due = Stopwatch.GetTimestamp() + (long)(wait * Stopwatch.Frequency);
                    _pending.Writer.TryWrite(new Scheduled(epoch, due, window));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _pending.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (item.Epoch != Volatile.Read(ref _epoch))
                {
                    continue; // scheduled before a flush — audio that will never sound
                }

                var wait = (item.DueStopwatchTimestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
                    if (item.Epoch != Volatile.Read(ref _epoch))
                    {
                        continue;
                    }
                }
                else if (wait < -0.25)
                {
                    // Late light frames would visibly trail the music — skip
                    // forward instead of replaying a stale show.
                    if (++_lateDropped % 200 == 1)
                    {
                        _logger.LogWarning("light frames running {Ms:F0} ms late — skipping to catch up", -wait * 1000);
                    }

                    continue;
                }

                foreach (var device in _devices.Devices)
                {
                    if (device.Mode == WledLightMode.Off)
                    {
                        continue;
                    }

                    var rgb = device.Renderer.Render(device.Mode, item.Window, device.LedCount, device.RenderSettings);
                    WledLightRenderer.Scale(rgb, device.Brightness);
                    try
                    {
                        await device.SendPixelsAsync(rgb, 0, ct).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // Device already logged it; one dark strip must not stop the show.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task ReleaseAllAsync()
    {
        foreach (var device in _devices.Devices)
        {
            if (device.Mode == WledLightMode.Off)
            {
                continue;
            }

            try
            {
                // Blank now (the speakers just went silent), then let the
                // device's own idle effect take over a second later.
                await device.SendPixelsAsync(new byte[device.LedCount * 3]).ConfigureAwait(false);
                await device.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Best-effort courtesy; the realtime timeout covers it anyway.
            }
        }
    }
}
