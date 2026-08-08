using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Streamsemble.AirPlay.Sender.AirPlay2;
using Streamsemble.Core.Video;
using Streamsemble.Discovery;
using Streamsemble.Timing.Ptp;

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
    double? LeadMs);

/// <summary>
/// The outbound video sink: forwards the mirrored screen to the TV on the same
/// timeline the speakers are playing on.
///
/// Scheduling is the whole job, and it is the exact analogue of what
/// <see cref="AirPlayTargetGroup"/> does for audio. Every frame arrives carrying
/// <c>TargetNanos</c> — the instant the Mac wanted that picture visible, on the
/// grandmaster clock this hub owns — so the frame is sent one group latency
/// before that instant and stamped with the instant itself. The TV renders on
/// the stamp, the speakers render their audio on its stamp, and the two agree
/// because both stamps came from the same source and neither side had to
/// estimate a pipeline delay.
///
/// What this deliberately does NOT do is re-encode. The H.264 access units the
/// Mac produced go out untouched.
/// </summary>
public sealed class VideoTargetGroup : IVideoSink, IAsyncDisposable
{
    /// <summary>
    /// A frame later than this past its deadline is not worth sending: the TV
    /// would either drop it or show it late, and either way the stream is
    /// better served by resynchronising on the next keyframe. Generous relative
    /// to a frame interval so ordinary jitter does not trigger it.
    /// </summary>
    private static readonly TimeSpan MaxLateness = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How far ahead of its render stamp a frame goes on the wire — the
    /// latencyMs the SETUP declares, i.e. the buffer the display agreed to
    /// keep. A mirror display is a realtime renderer: fed a group latency
    /// early (as run 3 did), it has nowhere to hold 1.5 s of video and the
    /// timeline collapses to render-on-arrival. The deep buffer lives here,
    /// on our side of the wire, instead.
    /// </summary>
    private static readonly TimeSpan SendLead = TimeSpan.FromMilliseconds(100);

    private readonly IOptions<AirPlaySenderOptions> _options;
    private readonly AirPlayBrowser _browser;
    private readonly ILogger<VideoTargetGroup> _logger;
    private readonly MirrorNtpServer _timing;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MirrorSenderSession? _session;
    private VideoCodecConfig? _config;

    /// <summary>
    /// Set after a frame is skipped: the decoder's reference chain is broken, so
    /// everything until the next keyframe would decode to garbage anyway.
    /// </summary>
    private bool _awaitingKeyframe;

    /// <summary>Whether any picture has reached the display yet this session.</summary>
    private bool _started;

    /// <summary>
    /// Set from the first picture until the stream reaches the live edge. The
    /// post-connect backlog is late by construction — every frame in it spent
    /// the connect queued — but it is the only reference chain there is (the
    /// source sends one IDR and no more), so lateness must not drop any of it:
    /// the display fast-forwards through the backlog instead, and normal
    /// deadline enforcement resumes once a frame arrives inside the window.
    /// </summary>
    private bool _catchingUp;

    private double? _lastLeadMs;

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
        LeadMs: _lastLeadMs);

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
            _awaitingKeyframe = true;
            _started = false;
            _catchingUp = false;
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
            _awaitingKeyframe = true;
        }
    }

    public async ValueTask WriteAsync(VideoFrame frame, CancellationToken ct = default)
    {
        if (_session is not { IsAlive: true } session)
        {
            return;
        }

        // Ride the audio's shifted timeline: same source, same lateness.
        if (frame.TargetNanos > 0 && AudioTimelineShiftNanos?.Invoke() is > 0 and var shiftNanos)
        {
            frame = frame with { TargetNanos = frame.TargetNanos + shiftNanos };
        }

        // Nothing has been shown yet, so there is no timeline to protect and
        // no picture to preserve: take the first keyframe and send it NOW,
        // however late it is. Connecting to the display takes seconds, and the
        // frame that triggered the connect is stale by the time the session is
        // up — measuring it against its deadline drops it, re-arms the
        // keyframe wait, and leaves the display on "connected" with nothing to
        // decode until the sender happens to emit another IDR.
        if (!_started)
        {
            if (!frame.IsKeyframe)
            {
                session.NoteDroppedFrame();
                return;
            }

            _started = true;
            _awaitingKeyframe = false;
            _catchingUp = true;
            _logger.LogInformation(
                "{Name}: first picture sent ({LateMs:F0} ms behind its deadline — the stream catches up from here)",
                session.DisplayName, (PtpReceiverClock.NowNanos - frame.TargetNanos) / 1e6);
            await session.SendFrameAsync(frame, ct).ConfigureAwait(false);
            return;
        }

        // An unstamped frame has no schedule to keep — the inbound timing
        // exchange has not locked yet — so it goes out immediately rather than
        // being measured against a deadline that does not exist.
        if (frame.TargetNanos > 0)
        {
            var leadNanos = frame.TargetNanos - PtpReceiverClock.NowNanos;
            _lastLeadMs = leadNanos / 1e6;

            if (leadNanos < -MaxLateness.TotalMilliseconds * 1_000_000)
            {
                // Late frames in the catch-up backlog are sent anyway: they
                // are the reference chain, and the next keyframe that would
                // let the stream re-enter after a drop is not coming.
                if (!_catchingUp)
                {
                    session.NoteDroppedFrame();
                    _awaitingKeyframe = true;
                    return;
                }
            }
            else if (_catchingUp)
            {
                _catchingUp = false;
                _logger.LogInformation(
                    "{Name}: caught up to the live edge ({LeadMs:F0} ms lead)",
                    session.DisplayName, leadNanos / 1e6);
            }

            // Hold the frame here until one send-lead before its render
            // stamp. Anything already inside that window (all of a catch-up
            // backlog) goes now.
            var sendAtNanos = frame.TargetNanos - SendLead.Ticks * 100;
            var waitNanos = sendAtNanos - PtpReceiverClock.NowNanos;
            if (waitNanos > 2_000_000)
            {
                await Task.Delay(TimeSpan.FromTicks(waitNanos / 100), ct).ConfigureAwait(false);
            }
        }

        if (_awaitingKeyframe)
        {
            if (!frame.IsKeyframe)
            {
                session.NoteDroppedFrame();
                return;
            }

            _awaitingKeyframe = false;
            _logger.LogInformation("{Name}: resynchronised on a keyframe", session.DisplayName);
        }

        await session.SendFrameAsync(frame, ct).ConfigureAwait(false);
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
            _started = false;
            _catchingUp = false;
            _lastLeadMs = null;
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
