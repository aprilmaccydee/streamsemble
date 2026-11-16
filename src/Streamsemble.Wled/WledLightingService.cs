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
/// group latency earlier. The mapping is asked again right up to the send:
/// a window analyzed before the speakers anchored, or held across a
/// re-anchor, still lands on the timeline that actually plays it.
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
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

    private readonly AudioLightAnalyzer _analyzer = new();
    private int _epoch;
    private int _resetPending;
    private long _lateDropped;

    // How long the send loop sleeps before asking the sink again when a
    // window's audio turns audible, and how close counts as "now".
    private const double RecheckSeconds = 0.05;
    private const double SendSlackSeconds = 0.001;

    /// <param name="OnsetSample">Capture sample whose audible instant this window lands on.</param>
    /// <param name="FallbackDueTimestamp">Stopwatch deadline used only while the sink has no timeline.</param>
    private sealed record Scheduled(int Epoch, long OnsetSample, long FallbackDueTimestamp, LightWindow Window);

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
                    // Only the fallback deadline is fixed here; the send
                    // loop resolves the real one against the sink's timeline
                    // as late as it can.
                    var fallbackDue = Stopwatch.GetTimestamp() + (long)(_fallbackLatencySeconds * Stopwatch.Frequency);
                    _pending.Writer.TryWrite(new Scheduled(
                        epoch, window.StartSample + AudioLightAnalyzer.OnsetOffsetSamples, fallbackDue, window));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Seconds until this window's audio turns audible. The sink's
    /// own timeline answers whenever it has one; unanchored (nothing playing
    /// yet, or a WAV/null sink) falls back to the configured latency counted
    /// from when the window was analyzed.</summary>
    private double SecondsUntilDue(Scheduled item)
        => _secondsUntilAudible(item.OnsetSample)
           ?? (item.FallbackDueTimestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;

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

                // Sleep toward the deadline in short steps, re-asking the
                // sink each time: the answer given a group latency ago goes
                // stale when the speakers anchor or re-anchor in between.
                var wait = SecondsUntilDue(item);
                var current = true;
                while (wait > SendSlackSeconds)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, RecheckSeconds)), ct).ConfigureAwait(false);
                    if (item.Epoch != Volatile.Read(ref _epoch))
                    {
                        current = false;
                        break;
                    }

                    wait = SecondsUntilDue(item);
                }

                if (!current)
                {
                    continue;
                }

                if (wait < -0.25)
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
