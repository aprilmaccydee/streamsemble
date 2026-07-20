using Streamsemble.AirPlay.Sender;
using Streamsemble.Core;
using Streamsemble.Core.Abstractions;
using Streamsemble.Discovery;

namespace Streamsemble.Host;

public static class WebApi
{
    public sealed record TargetDto(string? Name, string? Host, int Port = 5000, string Protocol = "Auto", int LatencyTrimMs = 0);

    public sealed record SelectRequest(List<TargetDto> Targets);

    public sealed record VolumeRequest(float Volume);

    public sealed record SpeakerVolumeRequest(string Name, float Volume);

    public static void MapStreamsembleApi(this WebApplication app)
    {
        app.MapGet("/api/state", (
            DiscoveredTargetStore discovered,
            SelectedTargetStore selected,
            AirPlayTargetGroup group,
            PlaybackStatus status) =>
        {
            var meta = status.Metadata;
            var speakers = group.SpeakerStatuses();
            var telemetry = group.Telemetry();
            // The headline volume is what the speakers are actually at (their
            // mean), not the last slider position; slider position is the
            // fallback while nothing is connected/known.
            var averageVolume = group.AverageSpeakerVolume;
            return Results.Json(new
            {
                activeSource = status.ActiveSource,
                nowPlaying = new { title = meta.Title, artist = meta.Artist, album = meta.Album },
                volume = averageVolume ?? status.Volume,
                volumeIsAverage = averageVolume.HasValue,
                discovered = discovered.Current(DateTimeOffset.UtcNow)
                    .Select(s => new { id = s.Id, name = s.Name, host = s.Host, port = s.Port, protocol = s.Protocol }),
                selected = selected.Current
                    .Select(t => new { name = t.Name, host = t.Host, port = t.Port, protocol = t.Protocol }),
                connected = group.ConnectedTargetNames,
                speakers = speakers.Select(sp => new
                {
                    name = sp.Name,
                    volume = sp.Volume,
                    targetLeadMs = sp.TargetLeadMs,
                    aheadMs = sp.AheadMs,
                    ptpLockAgeSeconds = sp.PtpLockAgeSeconds,
                    protocol = sp.Telemetry.Protocol,
                    mode = sp.Telemetry.Mode,
                    pairing = sp.Telemetry.Pairing,
                    alive = sp.Telemetry.Alive,
                    latencyTrimMs = sp.Telemetry.LatencyTrimMs,
                    reportedLatencyMs = sp.Telemetry.ReportedLatencyMs,
                    anchored = sp.Telemetry.Anchored,
                    encoderAgeMs = sp.Telemetry.EncoderAgeMs,
                    inheritedDebtMs = sp.Telemetry.InheritedDebtMs,
                    bufferedPacketsSent = sp.Telemetry.BufferedPacketsSent,
                    timelineId = sp.Telemetry.TimelineId,
                }),
                telemetry = new
                {
                    streaming = telemetry.Streaming,
                    holding = telemetry.Holding,
                    realtimePacketsSent = telemetry.RealtimePacketsSent,
                    realtimePacketRate = telemetry.RealtimePacketRate,
                    sendQueueDepth = telemetry.SendQueueDepth,
                    rtpHead = telemetry.RtpHead,
                    epoch = telemetry.Epoch is { } epoch
                        ? new { captureSample = epoch.CaptureSample, anchorUnixSeconds = epoch.AnchorUnixSeconds }
                        : null,
                    ptpClockId = telemetry.PtpClockId,
                    presentationLatencyMs = telemetry.PresentationLatencyMs,
                },
            });
        });

        app.MapPost("/api/speakers/volume", async (SpeakerVolumeRequest request, AirPlayTargetGroup group) =>
        {
            var volume = Math.Clamp(request.Volume, 0f, 1f);
            return await group.SetSpeakerVolumeAsync(request.Name, volume)
                ? Results.Ok(new { name = request.Name, volume })
                : Results.NotFound(new { error = $"no live session named \"{request.Name}\"" });
        });

        app.MapPost("/api/targets", (SelectRequest request, SelectedTargetStore selected) =>
        {
            var targets = request.Targets.Select(t => new AirPlayTargetOptions
            {
                Name = t.Name,
                Host = t.Host,
                Port = t.Port,
                Protocol = t.Protocol,
                LatencyTrimMs = t.LatencyTrimMs,
            }).ToList();
            selected.Set(targets);
            return Results.Ok(new { count = targets.Count });
        });

        app.MapPost("/api/volume", async (VolumeRequest request, IAudioSink sink, PlaybackStatus status) =>
        {
            var volume = Math.Clamp(request.Volume, 0f, 1f);
            status.Volume = volume;
            await sink.SetVolumeAsync(volume);
            return Results.Ok(new { volume });
        });

        // Debug endpoints driving the TONE SOURCE's state exactly like
        // librespot's player events drive the Spotify source — the pump then
        // runs its real pause/resume/cutover paths, scriptable via curl
        // against a recording receiver instead of debugged by ear.
        app.MapPost("/api/debug/pause", (Streamsemble.Core.Audio.ToneSource tone) =>
        {
            tone.DebugPause();
            return Results.Ok(new { op = "pause" });
        });

        app.MapPost("/api/debug/resume", (Streamsemble.Core.Audio.ToneSource tone) =>
        {
            tone.DebugResume();
            return Results.Ok(new { op = "resume" });
        });

        app.MapPost("/api/debug/cutover", (Streamsemble.Core.Audio.ToneSource tone) =>
        {
            tone.DebugCutover();
            return Results.Ok(new { op = "cutover" });
        });
    }
}
