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

    public static void MapStreamsembleApi(this WebApplication app)
    {
        app.MapGet("/api/state", (
            DiscoveredTargetStore discovered,
            SelectedTargetStore selected,
            AirPlayTargetGroup group,
            PlaybackStatus status) =>
        {
            var meta = status.Metadata;
            return Results.Json(new
            {
                activeSource = status.ActiveSource,
                nowPlaying = new { title = meta.Title, artist = meta.Artist, album = meta.Album },
                volume = status.Volume,
                discovered = discovered.Current(DateTimeOffset.UtcNow)
                    .Select(s => new { id = s.Id, name = s.Name, host = s.Host, port = s.Port, protocol = s.Protocol }),
                selected = selected.Current
                    .Select(t => new { name = t.Name, host = t.Host, port = t.Port, protocol = t.Protocol }),
                connected = group.ConnectedTargetNames,
            });
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
    }
}
