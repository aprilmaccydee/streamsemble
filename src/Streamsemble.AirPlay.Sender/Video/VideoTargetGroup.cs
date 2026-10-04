using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Streamsemble.AirPlay.Sender.AirPlay2;
using Streamsemble.Core.Video;
using Streamsemble.Discovery;

namespace Streamsemble.AirPlay.Sender.Video;

/// <summary>Video pipeline snapshot for the web UI.</summary>
public sealed record VideoTelemetry(
    bool Streaming,
    string? TargetName,
    string? Resolution,
    string Pairing,
    long PacketsSent,
    long BytesSent,
    long FramesDropped,
    bool CarriesAudio,
    float? TargetVolume,
    long AudioPacketsSent,
    long TimingQueriesAnswered,
    double? LeadMs,
    bool HardSyncEnabled);

/// <summary>
/// The outbound video sink: forwards the mirrored screen to the TV on the same
/// timeline the speakers are playing on.
///
/// In hard-sync mode, each frame's <c>TargetNanos</c> rides the same shifted
/// group timeline as the audio. The deep delay stays in the hub; frames go on
/// the wire only 100 ms before their render stamp, within the display's own
/// buffer. Low-latency mode drains that queue in decoder order with fresh
/// near-current stamps, keeping the picture responsive while audio continues
/// on the group timeline. Switching modes does not restart either stream.
///
/// What this deliberately does NOT do is re-encode. The H.264 access units the
/// Mac produced go out untouched.
/// </summary>
public sealed class VideoTargetGroup : IVideoSink, IAsyncDisposable
{
    private readonly IOptions<AirPlaySenderOptions> _options;
    private readonly AirPlayBrowser _browser;
    private readonly ILogger<VideoTargetGroup> _logger;
    private readonly MirrorNtpServer _timing;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VideoFrameScheduler _scheduler = new();

    private MirrorSenderSession? _session;
    private VideoCodecConfig? _config;

    public VideoTargetGroup(
        IOptions<AirPlaySenderOptions> options,
        AirPlayBrowser browser,
        MirrorNtpServer timing,
        ILogger<VideoTargetGroup> logger)
    {
        _options = options;
        _browser = browser;
        _timing = timing;
        _logger = logger;
    }

    public bool Streaming => _session is { IsAlive: true };

    /// <summary>Whether the picture waits for the speaker group's audio timeline.</summary>
    public bool HardSyncEnabled => _scheduler.HardSyncEnabled;

    /// <summary>
    /// Changes video pacing immediately, including a frame already waiting.
    /// Low-latency mode leaves group audio alone while draining the complete
    /// decoder reference chain to catch the picture up to the live edge.
    /// </summary>
    public void SetHardSyncEnabled(bool enabled)
    {
        _scheduler.SetHardSyncEnabled(enabled);
        _logger.LogInformation("video hard sync {Mode}", enabled ? "enabled" : "disabled (low-latency picture)");
    }

    /// <summary>
    /// The audio group's uniform timeline shift (its <c>StampShiftNanos</c>),
    /// wired by the host. When the source's stamps lead by less than the
    /// speakers' group latency, the audio runs late by this much — so the
    /// picture must run late by exactly the same amount or the lips lead the
    /// voice. Applied to every stamped frame before scheduling and stamping.
    /// </summary>
    public Func<long>? AudioTimelineShiftNanos { get; set; }

    public VideoTelemetry Telemetry() => new(
        Streaming: Streaming,
        TargetName: _session?.DisplayName,
        Resolution: _config?.Describe(),
        Pairing: _session?.PairingMode ?? "none",
        PacketsSent: _session?.PacketsSent ?? 0,
        BytesSent: _session?.BytesSent ?? 0,
        FramesDropped: _session?.FramesDropped ?? 0,
        CarriesAudio: _session?.CarriesAudio ?? false,
        TargetVolume: _session?.LastKnownVolume,
        AudioPacketsSent: _session?.AudioPacketsSent ?? 0,
        TimingQueriesAnswered: _timing.QueriesAnswered,
        LeadMs: _scheduler.LastLeadMs,
        HardSyncEnabled: HardSyncEnabled);

    public async Task StartStreamAsync(VideoCodecConfig config, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                return;
            }

            if (VideoTarget() is not { } target)
            {
                _logger.LogWarning(
                    "screen mirroring has nothing to send to — set AirPlaySender:VideoTarget to the display's name or host");
                return;
            }

            var (name, address, port) = await ResolveAsync(target, ct).ConfigureAwait(false);
            var session = new MirrorSenderSession(name, address, port, _timing, _logger);
            try
            {
                await session.ConnectAsync(config, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Name}: could not start screen mirroring", name);
                session.Dispose();
                return;
            }

            _session = session;
            _config = config;
            _scheduler.Reset();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReconfigureAsync(VideoCodecConfig config, CancellationToken ct = default)
    {
        _config = config;
        if (_session is { } session)
        {
            await session.SendCodecConfigAsync(config, ct).ConfigureAwait(false);
            _scheduler.AwaitKeyframe();
        }
    }

    public async ValueTask WriteAsync(VideoFrame frame, CancellationToken ct = default)
    {
        if (_session is not { IsAlive: true } session)
        {
            return;
        }

        var scheduled = await _scheduler.ScheduleAsync(frame, AudioTimelineShiftNanos, ct).ConfigureAwait(false);
        if (!ReferenceEquals(_session, session))
        {
            return;
        }

        if (scheduled is not { } ready)
        {
            session.NoteDroppedFrame();
            return;
        }

        if (ready.FirstPicture)
        {
            _logger.LogInformation(
                "{Name}: first picture sent ({LeadMs:F0} ms lead — the stream catches up from here)",
                session.DisplayName, ready.LeadMs);
        }
        else if (ready.CaughtUp)
        {
            _logger.LogInformation("{Name}: caught up to the group timeline ({LeadMs:F0} ms lead)",
                session.DisplayName, ready.LeadMs);
        }

        await session.SendFrameAsync(ready.Frame, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the mirrored display's volume, addressed by name from the web UI.
    /// While a display shows the mirrored screen its one AirPlay session is
    /// the mirror, so the speaker group cannot reach it — volume requests for
    /// it land here instead. False when no live audio-carrying mirror session
    /// matches the name; same substring-either-way match as the resolver, so
    /// the configured short name and the advertised display name both work.
    /// </summary>
    public async Task<bool> SetTargetVolumeAsync(string name, float volume, CancellationToken ct = default)
    {
        if (_session is not { IsAlive: true, CarriesAudio: true } session
            || string.IsNullOrWhiteSpace(name)
            || (!session.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)
                && !name.Contains(session.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return await session.SetVolumeAsync(Math.Clamp(volume, 0f, 1f), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The master slider: the mirrored display plays the group's audio, so it
    /// follows a set-everything request like any speaker. A no-op when nothing
    /// is mirroring or the mirror carries no audio.
    /// </summary>
    public async Task SetGroupVolumeAsync(float volume, CancellationToken ct = default)
    {
        if (_session is { IsAlive: true, CarriesAudio: true } session)
        {
            await session.SetVolumeAsync(Math.Clamp(volume, 0f, 1f), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The mirrored display's audio, called by the speaker group's send loop
    /// with each frame it just paced. The frame goes to the mirror session's
    /// companion audio stream carrying the same audible instant the speakers
    /// render on. A no-op whenever nothing is mirroring — the display is an
    /// ordinary speaker-group member then and gets its audio the ordinary way.
    /// </summary>
    public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, long audibleNanos, CancellationToken ct = default)
        => _session is { IsAlive: true } session
            ? session.SendAudioAsync(pcm, audibleNanos, ct)
            : ValueTask.CompletedTask;

    public async Task StopStreamAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is not { } session)
            {
                return;
            }

            _session = null;
            _config = null;
            _scheduler.Reset();
            await session.TeardownAsync(CancellationToken.None).ConfigureAwait(false);
            session.Dispose();
            _logger.LogInformation("{Name}: screen mirroring stopped", session.DisplayName);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AirPlayTargetOptions? VideoTarget()
    {
        var configured = _options.Value.VideoTarget;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        // Match the same way the speaker targets do: a configured name is a
        // substring of the advertised one, so "Living Room" finds the TV.
        return _options.Value.Targets.FirstOrDefault(t =>
                   (t.Name?.Equals(configured, StringComparison.OrdinalIgnoreCase) ?? false)
                   || (t.Host?.Equals(configured, StringComparison.OrdinalIgnoreCase) ?? false))
               ?? new AirPlayTargetOptions { Name = configured };
    }

    private async Task<(string Name, IPAddress Address, int Port)> ResolveAsync(
        AirPlayTargetOptions target, CancellationToken ct)
    {
        if (target.Host is { } host)
        {
            var address = IPAddress.TryParse(host, out var literal)
                ? literal
                : (await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false))
                    .OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
                    .First();
            return (target.Name ?? host, address, target.Port == 5000 ? 7000 : target.Port);
        }

        var discovered = await _browser.BrowseAsync(TimeSpan.FromSeconds(_options.Value.ScanSeconds), ct)
            .ConfigureAwait(false);
        var match = discovered.FirstOrDefault(t => t.DisplayName.Contains(target.Name!, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException(
                $"\"{target.Name}\" not found via mDNS (saw: "
                + $"{string.Join(", ", discovered.Select(d => d.DisplayName).DefaultIfEmpty("nothing"))})");
        return (match.DisplayName, match.Address, match.AirPlayPort ?? 7000);
    }

    /// <summary>
    /// Registered both as itself and as <see cref="IVideoSink"/>, so the
    /// container disposes this instance once per registration. Disposal has to
    /// be idempotent or the second pass finds the gate already gone.
    /// </summary>
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopStreamAsync().ConfigureAwait(false);
        _timing.Dispose();
        _gate.Dispose();
    }
}
