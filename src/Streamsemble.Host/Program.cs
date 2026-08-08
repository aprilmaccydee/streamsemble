using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Streamsemble.AirPlay.Receiver;
using Streamsemble.AirPlay.Receiver.Video;
using Streamsemble.AirPlay.Sender;
using Streamsemble.AirPlay.Sender.AirPlay2;
using Streamsemble.AirPlay.Sender.Video;
using Streamsemble.Cast.Stub;
using Streamsemble.Core;
using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Audio;
using Streamsemble.Core.Video;
using Streamsemble.Discovery;
using Streamsemble.Host;
using Streamsemble.Spotify;
using Streamsemble.Timing;
using Streamsemble.Timing.Ptp;
using Streamsemble.Wled;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StreamsembleOptions>(builder.Configuration.GetSection("Streamsemble"));
builder.Services.Configure<SpotifyOptions>(builder.Configuration.GetSection("Spotify"));
builder.Services.Configure<AirPlaySenderOptions>(builder.Configuration.GetSection("AirPlaySender"));
builder.Services.Configure<AirPlayReceiverOptions>(builder.Configuration.GetSection("AirPlayReceiver"));
builder.Services.Configure<WledOptions>(builder.Configuration.GetSection("Wled"));
// Inbound frames are released exactly one group latency before their
// stamped render deadline. The release time is sync-neutral — frames
// render at their stamp regardless — it only has to be early enough that
// the data is downstream when due (a frame released at stamp − latency is
// sent the moment it lands, still a full group latency before it renders).
// Respects STREAMSEMBLE_GROUP_LATENCY via the sender-side constant.
builder.Services.PostConfigure<AirPlayReceiverOptions>(
    o => o.PresentationLatencySamples = AirPlay2Session.GroupPresentationLatencySamples);

// Timing: exactly one master clock and timing responder for the whole process —
// every outbound speaker session must discipline to the same timeline.
builder.Services.AddSingleton<IMasterClock, MasterClock>();
builder.Services.AddSingleton<NtpTimingResponder>();
builder.Services.AddSingleton<AirPlayBrowser>();
builder.Services.AddSingleton<ServiceAdvertiser>();
builder.Services.AddSingleton<DiscoveredTargetStore>();
builder.Services.AddSingleton<SelectedTargetStore>();
builder.Services.AddSingleton<PlaybackStatus>();

// Pipeline
builder.Services.AddSingleton<ISourceArbiter, LastWriterWinsArbiter>();
// ONE grandmaster clock for the whole hub: the receiver serves it to
// inbound AirPlay senders and the speaker fan-out serves it to the group —
// every SETRATEANCHORTIME timeline is this clock.
builder.Services.AddSingleton(sp => new PtpReceiverClock(
    AirPlayReceiverService.BuildClockId(AirPlayReceiverService.BuildIdentity("clock").DeviceId),
    sp.GetRequiredService<ILogger<PtpReceiverClock>>()));
builder.Services.AddSingleton<AirPlayTargetGroup>();
builder.Services.AddSingleton<IAudioSink>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<StreamsembleOptions>>().Value;
    IAudioSink sink = opts.Sink.ToLowerInvariant() switch
    {
        "airplay" => sp.GetRequiredService<AirPlayTargetGroup>(),
        "wav" => new WavFileSink(opts.WavDirectory, sp.GetRequiredService<ILogger<WavFileSink>>()),
        "null" => new NullSink(),
        var other => throw new InvalidOperationException($"Unknown sink \"{other}\" (expected AirPlay, Wav or Null)"),
    };

    // With WLED strips configured, the lighting engine taps the exact frame
    // stream (and flush/stop signals) the real sink receives.
    return sp.GetRequiredService<IOptions<WledOptions>>().Value.Devices.Count > 0
        ? new LightingTapSink(sink, sp.GetRequiredService<WledLightingService>())
        : sink;
});
builder.Services.AddSingleton<AudioPump>();
builder.Services.AddHostedService<PumpService>();
builder.Services.AddHostedService<DiscoveryService>();

// Sources
builder.Services.AddSingleton(sp =>
{
    var spotify = sp.GetRequiredService<IOptions<SpotifyOptions>>().Value;
    var global = sp.GetRequiredService<IOptions<StreamsembleOptions>>().Value;
    return new LibrespotSource(spotify, global.DeviceName, sp.GetRequiredService<ILogger<LibrespotSource>>());
});
builder.Services.AddSingleton<IAudioSource>(sp => sp.GetRequiredService<LibrespotSource>());
if (builder.Configuration.GetValue("Spotify:Enabled", true))
{
    builder.Services.AddHostedService<LibrespotSourceService>();
}

if (builder.Configuration.GetValue("Streamsemble:TestTone", false))
{
    builder.Services.AddSingleton<ToneSource>();
    builder.Services.AddSingleton<IAudioSource>(sp => sp.GetRequiredService<ToneSource>());
    builder.Services.AddHostedService<ToneSourceService>();
}

builder.Services.AddSingleton<AirPlayReceiverSource>();
builder.Services.AddSingleton<IAudioSource>(sp => sp.GetRequiredService<AirPlayReceiverSource>());
// Screen mirroring's video half. It is a singleton alongside the audio source
// because one mirror session feeds both: the Mac's picture arrives on the
// type-110 stream while its sound keeps flowing through the audio path, and
// both are stamped on the same grandmaster clock so they stay together.
builder.Services.AddSingleton<MirrorVideoSource>();
builder.Services.AddHostedService<AirPlayReceiverService>();

// Video fan-out: the mirrored screen goes back out to the display named by
// AirPlaySender:VideoTarget. It rides the SAME grandmaster clock the speakers
// do — the mirror timing server serves that clock, and every frame carries the
// render deadline the Mac stated — so picture and sound land together without
// either side estimating the other's delay.
builder.Services.AddSingleton<MirrorNtpServer>(sp =>
    new MirrorNtpServer(sp.GetRequiredService<ILogger<MirrorNtpServer>>()));
builder.Services.AddSingleton<VideoTargetGroup>();
// While a display is showing the mirrored screen it gets its audio through the
// mirror session, so the speaker fan-out must not also stream to it — a TV that
// accepts only one AirPlay session at a time will drop the mirror when a second
// one arrives.
//
// The trigger is the INBOUND source going active, not the outbound session
// coming up: the speaker group reconciles as soon as audio starts flowing,
// which is before the outbound mirror has finished connecting, so keying off
// the outbound session loses the race and the TV gets both.
builder.Services.AddSingleton<IHostedService>(sp =>
{
    var audio = sp.GetRequiredService<AirPlayTargetGroup>();
    var mirror = sp.GetRequiredService<MirrorVideoSource>();
    var video = sp.GetRequiredService<VideoTargetGroup>();
    var configured = sp.GetRequiredService<IOptions<AirPlaySenderOptions>>().Value.VideoTarget;
    audio.ActiveVideoTargetName = () => mirror.IsActive ? configured : null;

    // The mirror session carries a type-96 companion audio stream, so the
    // excluded display still gets sound — through the one session it can hold.
    // The send loop hands each paced frame (and its audible instant) over.
    audio.MirrorCarriesAudio = true;
    audio.MirrorAudioSink = video.WriteAudioAsync;

    // A realtime mirror's stamps lead by less than the speakers' group
    // latency, so the audio timeline runs uniformly late by the deficit; the
    // picture must trail by exactly the same amount to keep lip sync.
    video.AudioTimelineShiftNanos = () => audio.StampShiftNanos;

    // And if the group is ALREADY streaming to that TV when mirroring starts,
    // reconcile again so it is dropped (and taken back when mirroring ends).
    mirror.ActiveChanged += (_, _) => _ = audio.ReconcileAsync(CancellationToken.None);
    return new NoopHostedService();
});
builder.Services.AddSingleton<IVideoSink>(sp => sp.GetRequiredService<VideoTargetGroup>());
builder.Services.AddSingleton(sp => new VideoPump(
    sp.GetRequiredService<MirrorVideoSource>(),
    sp.GetRequiredService<IVideoSink>(),
    sp.GetRequiredService<ILogger<VideoPump>>()));
builder.Services.AddHostedService<VideoPumpService>();

// Lighting: WLED UDP realtime. The lighting service turns the tapped frame
// stream into per-strip light shows, and schedules every light frame on the
// group's capture→audible timeline so the strips land on the beat the
// LISTENER hears — the same instant the speakers render, not the group
// latency earlier when the pump saw the bytes. /api/wled/* gives manual
// control and runtime tuning.
builder.Services.AddSingleton(sp => new WledDeviceGroup(
    sp.GetRequiredService<IOptions<WledOptions>>().Value,
    sp.GetRequiredService<ILogger<WledDeviceGroup>>()));
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<StreamsembleOptions>>().Value;
    var airplay = opts.Sink.Equals("airplay", StringComparison.OrdinalIgnoreCase);
    var group = airplay ? sp.GetRequiredService<AirPlayTargetGroup>() : null;
    return new WledLightingService(
        sp.GetRequiredService<WledDeviceGroup>(),
        group is null ? _ => 0.0 : group.SecondsUntilAudible,
        airplay ? AirPlay2Session.GroupPresentationLatencySeconds : 0.0,
        sp.GetRequiredService<ILogger<WledLightingService>>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<WledLightingService>());

builder.Services.AddSingleton<CastStubSource>();
builder.Services.AddSingleton<IAudioSource>(sp => sp.GetRequiredService<CastStubSource>());
builder.Services.AddHostedService<CastStubService>();

var app = builder.Build();

// Seed the selectable targets from configuration so appsettings/CLI targets
// still work headlessly; the web UI overrides this set at runtime.
var selected = app.Services.GetRequiredService<SelectedTargetStore>();
var configuredTargets = app.Services.GetRequiredService<IOptions<AirPlaySenderOptions>>().Value.Targets;
if (configuredTargets.Count > 0)
{
    selected.Set(configuredTargets);
}

// Fail fast on WLED config typos (bad Mode, missing Host) instead of
// surfacing them as 500s on the first /api/wled call.
_ = app.Services.GetRequiredService<WledDeviceGroup>();

app.UseDefaultFiles();
// no-cache ≠ don't cache: the browser may keep a copy but must revalidate
// before using it (a 304 when unchanged). Without this there is no
// Cache-Control at all and browsers cache heuristically off Last-Modified —
// after a redeploy the UI keeps rendering with the previous page's script
// against the new API until someone thinks to hard-refresh.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapStreamsembleApi();
app.MapFallbackToFile("index.html");

await app.RunAsync();
