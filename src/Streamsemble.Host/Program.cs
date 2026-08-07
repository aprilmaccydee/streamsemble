using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Streamsemble.AirPlay.Receiver;
using Streamsemble.AirPlay.Sender;
using Streamsemble.AirPlay.Sender.AirPlay2;
using Streamsemble.Cast.Stub;
using Streamsemble.Core;
using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Audio;
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
builder.Services.AddHostedService<AirPlayReceiverService>();

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
app.UseStaticFiles();
app.MapStreamsembleApi();
app.MapFallbackToFile("index.html");

await app.RunAsync();
