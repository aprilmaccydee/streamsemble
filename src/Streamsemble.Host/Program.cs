using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Streamsemble.AirPlay.Receiver;
using Streamsemble.AirPlay.Sender;
using Streamsemble.Cast.Stub;
using Streamsemble.Core;
using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Audio;
using Streamsemble.Discovery;
using Streamsemble.Host;
using Streamsemble.Spotify;
using Streamsemble.Timing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StreamsembleOptions>(builder.Configuration.GetSection("Streamsemble"));
builder.Services.Configure<SpotifyOptions>(builder.Configuration.GetSection("Spotify"));
builder.Services.Configure<AirPlaySenderOptions>(builder.Configuration.GetSection("AirPlaySender"));
builder.Services.Configure<AirPlayReceiverOptions>(builder.Configuration.GetSection("AirPlayReceiver"));

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
builder.Services.AddSingleton<AirPlayTargetGroup>();
builder.Services.AddSingleton<IAudioSink>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<StreamsembleOptions>>().Value;
    return opts.Sink.ToLowerInvariant() switch
    {
        "airplay" => sp.GetRequiredService<AirPlayTargetGroup>(),
        "wav" => new WavFileSink(opts.WavDirectory, sp.GetRequiredService<ILogger<WavFileSink>>()),
        "null" => new NullSink(),
        var other => throw new InvalidOperationException($"Unknown sink \"{other}\" (expected AirPlay, Wav or Null)"),
    };
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

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapStreamsembleApi();
app.MapFallbackToFile("index.html");

await app.RunAsync();
