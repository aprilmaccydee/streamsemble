using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Streamsemble.AirPlay.Sender;
using Streamsemble.Core;
using Streamsemble.Core.Abstractions;
using Streamsemble.Discovery;
using Streamsemble.Wled;

namespace Streamsemble.Host;

public static class WebApi
{
    public sealed record TargetDto(string? Name, string? Host, int Port = 5000, string Protocol = "Auto", int LatencyTrimMs = 0);

    public sealed record SelectRequest(List<TargetDto> Targets);

    public sealed record VolumeRequest(float Volume);

    public sealed record SpeakerVolumeRequest(string Name, float Volume);

    public sealed record WledColorRequest(byte R, byte G, byte B, string? Device = null);

    public sealed record WledPixelsRequest(byte[][] Pixels, int Start = 0, string? Device = null);

    public sealed record WledConfigRequest(string? Device = null, string? Mode = null, string? Color = null, float? Brightness = null);

    public static void MapStreamsembleApi(this WebApplication app)
    {
        app.MapGet("/api/state", (
            DiscoveredTargetStore discovered,
            SelectedTargetStore selected,
            AirPlayTargetGroup group,
            WledDeviceGroup wled,
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
                nowPlaying = new
                {
                    title = meta.Title,
                    artist = meta.Artist,
                    album = meta.Album,
                    albumArtist = meta.AlbumArtist,
                    trackNumber = meta.TrackNumber,
                    discNumber = meta.DiscNumber,
                    durationMs = meta.Duration?.TotalMilliseconds,
                    positionMs = meta.Position?.TotalMilliseconds,
                    // Versioned by track identity so the browser refetches on
                    // track change but caches within one track.
                    artworkUrl = meta.Artwork is { Length: > 0 } ? $"/api/artwork?v={meta.PersistentId():x}" : null,
                    artworkSourceUrl = meta.ArtworkUrl,
                },
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
                wled = wled.Devices.Select(d => new
                {
                    name = d.Name,
                    mode = d.Mode.ToString(),
                    color = d.ColorHex,
                    brightness = d.Brightness,
                    ledCount = d.LedCount,
                    protocol = d.Protocol.ToString(),
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

        // Cover art for the current track, straight from the source's metadata
        // — the same bytes the speakers were sent, so what the browser shows is
        // proof of what went out.
        app.MapGet("/api/artwork", (PlaybackStatus status) =>
        {
            var meta = status.Metadata;
            return meta.Artwork is { Length: > 0 } art
                ? Results.File(art, meta.ArtworkMimeType ?? "image/jpeg")
                : Results.NotFound();
        });

        app.MapPost("/api/speakers/volume", async (SpeakerVolumeRequest request, AirPlayTargetGroup group) =>
        {
            var volume = Math.Clamp(request.Volume, 0f, 1f);
            return await group.SetSpeakerVolumeAsync(request.Name, volume)
                ? Results.Ok(new { name = request.Name, volume })
                : Results.NotFound(new { error = $"no live session named \"{request.Name}\"" });
        });

        app.MapPost("/api/targets", (SelectRequest request, SelectedTargetStore selected, IOptions<AirPlaySenderOptions> sender) =>
        {
            // A UI selection carries only a discovered name + detected protocol;
            // a configured target for the same speaker carries the operator's
            // intent (stream mode, latency trim, host/port). Joining from the UI
            // must not erase that, so a matching configured entry wins over a
            // bare DTO. Same match semantics as the mDNS resolver: the config
            // name is a substring of the discovered display name; most specific
            // configured name wins.
            var configured = sender.Value.Targets.Where(c => !string.IsNullOrEmpty(c.Name)).ToList();
            var targets = request.Targets.Select(t =>
                (t.Name is { } dtoName
                    ? configured
                        .Where(c => dtoName.Contains(c.Name!, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(c => c.Name!.Length)
                        .FirstOrDefault()
                    : null)
                ?? new AirPlayTargetOptions
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

        // WLED UDP realtime: push RGB straight to the configured LED strips.
        // A strip shows exactly what it is sent and returns to its own effect
        // once packets stop (after its TimeoutSeconds). "device" targets one
        // strip by name; omitted, every strip gets the data.
        app.MapPost("/api/wled/color", (WledColorRequest request, WledDeviceGroup wled) =>
            SendWledAsync(wled, request.Device, (d, ct) => d.SendColorAsync(request.R, request.G, request.B, ct)));

        app.MapPost("/api/wled/pixels", (WledPixelsRequest request, WledDeviceGroup wled) =>
        {
            if (request.Pixels.Any(p => p.Length != 3))
            {
                return Task.FromResult(Results.BadRequest(new { error = "each pixel must be an [r, g, b] triple" }));
            }

            var rgb = new byte[request.Pixels.Length * 3];
            for (var i = 0; i < request.Pixels.Length; i++)
            {
                request.Pixels[i].CopyTo(rgb, i * 3);
            }

            return SendWledAsync(wled, request.Device, (d, ct) => d.SendPixelsAsync(rgb, request.Start, ct));
        });

        app.MapPost("/api/wled/release", (string? device, WledDeviceGroup wled) =>
            SendWledAsync(wled, device, (d, ct) => d.ReleaseAsync(ct)));

        // Runtime tuning (web UI): light mode (Off = disabled), Pulse color,
        // master brightness — per device, or every device when none is named.
        // Takes effect on the next light frame, ~25 ms.
        app.MapPost("/api/wled/config", (WledConfigRequest request, WledDeviceGroup wled) =>
        {
            if (!wled.IsConfigured)
            {
                return Results.Json(new { error = "no WLED devices configured (Wled:Devices)" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var targets = wled.Match(request.Device);
            if (targets.Count == 0)
            {
                return Results.NotFound(new { error = $"no WLED device named \"{request.Device}\"" });
            }

            WledLightMode? mode;
            (byte, byte, byte)? color;
            try
            {
                mode = request.Mode is { } m ? WledLightModes.Parse(m) : null;
                color = request.Color is { } c ? WledDevice.ParseColor(c) : null;
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            foreach (var target in targets)
            {
                if (mode is { } newMode)
                {
                    target.Mode = newMode;
                }

                if (color is { } newColor)
                {
                    target.Color = newColor;
                }

                if (request.Brightness is { } brightness)
                {
                    target.Brightness = Math.Clamp(brightness, 0f, 1f);
                }

                if (mode is WledLightMode.Off)
                {
                    // Blank instead of freezing the last light frame, and let
                    // the device's own idle effect take over a second later.
                    _ = ReleaseQuietlyAsync(target);
                }
            }

            return Results.Ok(new
            {
                devices = targets.Select(t => new
                {
                    name = t.Name,
                    mode = t.Mode.ToString(),
                    color = t.ColorHex,
                    brightness = t.Brightness,
                }),
            });
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

    private static async Task ReleaseQuietlyAsync(WledDevice device)
    {
        try
        {
            await device.SendPixelsAsync(new byte[device.LedCount * 3]);
            await device.ReleaseAsync();
        }
        catch (SocketException)
        {
            // The device logged it; the realtime timeout covers us anyway.
        }
    }

    private static async Task<IResult> SendWledAsync(WledDeviceGroup wled, string? device, Func<WledDevice, CancellationToken, Task> send)
    {
        if (!wled.IsConfigured)
        {
            return Results.Json(new { error = "no WLED devices configured (Wled:Devices)" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var targets = wled.Match(device);
        if (targets.Count == 0)
        {
            return Results.NotFound(new { error = $"no WLED device named \"{device}\"" });
        }

        try
        {
            foreach (var target in targets)
            {
                await send(target, CancellationToken.None);
            }

            return Results.Ok(new { devices = targets.Select(t => t.Name) });
        }
        catch (SocketException ex)
        {
            return Results.Json(new { error = $"UDP send to WLED failed: {ex.Message}" },
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (ArgumentException ex)
        {
            // Data the device's mode cannot frame: too many LEDs for
            // DRGB/DRGBW/WARLS, an offset DRGB cannot express, partial pixels.
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
