using System.Diagnostics;
using System.Net;
using System.Web;
using Microsoft.Extensions.Logging;
using Streamsemble.Core.Audio;
using Streamsemble.Core.Metadata;

namespace Streamsemble.Spotify;

/// <summary>
/// Spotify Connect source backed by a supervised librespot child process.
/// Audio arrives as raw S16LE 44.1 kHz stereo PCM on librespot's stdout
/// (--backend pipe), already the pipeline's canonical format. Player events
/// (play/pause/track/volume) arrive via a generated --onevent shell script
/// that POSTs librespot's event env-vars to a localhost endpoint.
/// </summary>
public sealed class LibrespotSource(SpotifyOptions options, string defaultDeviceName, ILogger<LibrespotSource> logger)
    : AudioSourceBase("Spotify")
{
    private volatile TrackMetadata _metadata = new();

    /// <summary>Bumped on every track change; an in-flight cover fetch whose epoch is stale is discarded.</summary>
    private long _metadataEpoch;

    /// <summary>Spotify covers are ~50 KB; the cap is a sanity bound, not a target.</summary>
    private const int MaxArtworkBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// librespot exposes no external transport control, so preemption is
    /// passive: we mark the source idle and simply stop being pumped (the
    /// read loop keeps draining stdout so librespot never blocks on a full
    /// pipe). The Spotify app may still show "playing"; toggling pause/play
    /// there makes this source contend for the live slot again.
    /// </summary>
    public override Task StopAsync(CancellationToken cancellationToken = default)
    {
        SetState(Core.Abstractions.SourceState.Idle);
        return Task.CompletedTask;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var listener = StartEventListener(out var eventPort);
        _ = ListenForEventsAsync(listener, ct);
        _ = WatchPcmStallAsync(ct);
        var scriptPath = WriteEventScript(eventPort);

        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                await RunProcessOnceAsync(scriptPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "librespot failed");
            }

            SetState(Core.Abstractions.SourceState.Idle);
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Reset backoff after a healthy run; otherwise back off exponentially.
            backoff = DateTime.UtcNow - started > TimeSpan.FromMinutes(1)
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            logger.LogWarning("librespot exited; restarting in {Backoff}", backoff);
            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunProcessOnceAsync(string eventScriptPath, CancellationToken ct)
    {
        var deviceName = options.DeviceName ?? defaultDeviceName;
        var cacheDir = options.CacheDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".streamsemble", "librespot-cache");
        Directory.CreateDirectory(cacheDir);

        var psi = new ProcessStartInfo
        {
            FileName = options.LibrespotPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--name");
        psi.ArgumentList.Add(deviceName);
        psi.ArgumentList.Add("--backend");
        psi.ArgumentList.Add("pipe");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("S16");
        psi.ArgumentList.Add("--bitrate");
        psi.ArgumentList.Add(options.Bitrate.ToString());
        psi.ArgumentList.Add("--cache");
        psi.ArgumentList.Add(cacheDir);
        psi.ArgumentList.Add("--onevent");
        psi.ArgumentList.Add(eventScriptPath);
        foreach (var arg in (options.ExtraArgs ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {options.LibrespotPath}");
        logger.LogInformation("librespot started (pid {Pid}) as Spotify Connect device \"{Name}\"", process.Id, deviceName);

        var stderrTask = PumpStderrAsync(process, ct);
        try
        {
            await ReadPcmAsync(process.StandardOutput.BaseStream, ct).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private long _lastPcmAtMs = Environment.TickCount64;

    /// <summary>
    /// PCM flow is the authoritative playback signal (events are best-effort),
    /// so a stall while Active is the authoritative "playback died": go Idle
    /// so the sink silences and — after the arbiter's grace — releases the
    /// speakers. Ten seconds distinguishes a real death from librespot's
    /// track-load gaps; if PCM returns later, the read loop flips straight
    /// back to Active.
    /// </summary>
    private async Task WatchPcmStallAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (State == Core.Abstractions.SourceState.Active
                    && Environment.TickCount64 - Volatile.Read(ref _lastPcmAtMs) > 10_000)
                {
                    logger.LogWarning("no PCM for 10 s while Active — playback stalled, going idle");
                    SetState(Core.Abstractions.SourceState.Idle);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReadPcmAsync(Stream stdout, CancellationToken ct)
    {
        var frameBytes = PcmFrame.CanonicalFrameBytes;
        var sampleRate = AudioFormat.Canonical.SampleRate;
        var buffer = new byte[frameBytes];
        var filled = 0;

        // librespot's pipe backend emits PCM as fast as we read it — it is NOT
        // rate-limited. If we drain flat out, a 4-minute track is consumed in a
        // second and librespot races to end_of_track ("skipping tracks"). Pace
        // our reads to real time so the OS pipe back-pressures librespot into
        // playing at normal speed. The sink re-paces precisely downstream; this
        // only needs to be approximately real time.
        var stopwatch = Stopwatch.StartNew();
        long pacedSamples = 0;

        while (true)
        {
            var read = await stdout.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return; // librespot exited
            }

            filled += read;
            if (filled == frameBytes)
            {
                // Audio flowing is the authoritative "someone is playing" signal
                // (events are best-effort — they need curl on PATH).
                if (State == Core.Abstractions.SourceState.Idle)
                {
                    SetState(Core.Abstractions.SourceState.Active);
                }

                EmitPcm(buffer.AsMemory().ToArray());
                Volatile.Write(ref _lastPcmAtMs, Environment.TickCount64);
                filled = 0;

                pacedSamples += PcmFrame.SamplesPerFrame;
                var targetMs = pacedSamples * 1000.0 / sampleRate;
                var aheadMs = targetMs - stopwatch.Elapsed.TotalMilliseconds;
                if (aheadMs > 12)
                {
                    await Task.Delay((int)aheadMs, ct).ConfigureAwait(false);
                }
                else if (aheadMs < -1000)
                {
                    // Fell far behind real time (a pause, seek or underrun);
                    // re-anchor so we don't then burst to catch up.
                    stopwatch.Restart();
                    pacedSamples = 0;
                }
            }
        }
    }

    private async Task PumpStderrAsync(Process process, CancellationToken ct)
    {
        while (await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            logger.LogDebug("librespot: {Line}", line);
        }
    }

    private HttpListener StartEventListener(out int port)
    {
        port = options.EventPort != 0 ? options.EventPort : GetFreeTcpPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }

    private async Task ListenForEventsAsync(HttpListener listener, CancellationToken ct)
    {
        await using var _ = ct.Register(listener.Stop);
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                using var reader = new StreamReader(context.Request.InputStream);
                var form = HttpUtility.ParseQueryString(await reader.ReadToEndAsync(ct).ConfigureAwait(false));
                context.Response.StatusCode = 204;
                context.Response.Close();
                HandleEvent(form.AllKeys.OfType<string>().ToDictionary(k => k, k => form[k] ?? ""), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bad librespot event callback");
            }
        }
    }

    private void HandleEvent(Dictionary<string, string> vars, CancellationToken ct)
    {
        var playerEvent = vars.GetValueOrDefault("PLAYER_EVENT", "");
        logger.LogDebug("librespot event {Event}", playerEvent);
        switch (playerEvent)
        {
            case "playing":
                // Fresh grace for the watchdog: a play command precedes PCM by
                // however long the track takes to load.
                Volatile.Write(ref _lastPcmAtMs, Environment.TickCount64);
                SetState(Core.Abstractions.SourceState.Active);
                UpdatePosition(vars);
                break;
            case "paused":
                if (State == Core.Abstractions.SourceState.Active)
                {
                    SetState(Core.Abstractions.SourceState.Paused);
                }

                UpdatePosition(vars);
                break;
            case "seeked":
            case "position_correction":
                // The receiver's progress bar is only as good as the position
                // we last told it; a seek moves the playhead without changing
                // the track, so re-push the same listing item with fresh time.
                UpdatePosition(vars);
                break;
            case "loading":
                // A user-initiated load (skip/new selection): the pipeline's
                // queued tail belongs to the abandoned track — signal it for
                // discard. Natural gapless endings emit no "loading", so song
                // tails survive; plain pause keeps the tail for resume.
                if (State != Core.Abstractions.SourceState.Idle)
                {
                    Volatile.Write(ref _lastPcmAtMs, Environment.TickCount64);
                    RaiseDiscontinuity();
                    if (State == Core.Abstractions.SourceState.Active)
                    {
                        SetState(Core.Abstractions.SourceState.Paused);
                    }
                }

                break;
            case "stopped":
                SetState(Core.Abstractions.SourceState.Idle);
                break;
            case "session_disconnected":
                // The Spotify session/dealer dropping does NOT mean playback
                // stopped: librespot keeps decoding the current track and
                // reconnects within seconds (this WAN drops both Spotify TCP
                // flows every few minutes). Marking Idle here flushed and
                // re-anchored the speakers mid-song for nothing. If playback
                // actually died with the session, the PCM-stall watchdog
                // declares Idle shortly after.
                logger.LogInformation("librespot session disconnected — keeping state {State}; PCM watchdog will idle a real stall", State);
                break;
            case "track_changed":
                HandleTrackChanged(vars, ct);
                break;
            case "volume_changed":
                if (ushort.TryParse(vars.GetValueOrDefault("VOLUME"), out var volume))
                {
                    RaiseVolume(volume / 65535f);
                }

                break;
        }
    }

    /// <summary>
    /// New track: publish the text metadata immediately (receivers should show
    /// the title the moment audio starts, not after an HTTP round-trip), then
    /// fetch the cover in the background and republish once it lands.
    /// </summary>
    private void HandleTrackChanged(Dictionary<string, string> vars, CancellationToken ct)
    {
        var metadata = new TrackMetadata
        {
            Title = vars.GetValueOrDefault("NAME"),
            // librespot joins multiple artists with newlines; receivers want one line.
            Artist = LibrespotFields.JoinList(vars.GetValueOrDefault("ARTISTS")),
            Album = vars.GetValueOrDefault("ALBUM"),
            AlbumArtist = LibrespotFields.JoinList(vars.GetValueOrDefault("ALBUM_ARTISTS")),
            Duration = long.TryParse(vars.GetValueOrDefault("DURATION_MS"), out var ms)
                ? TimeSpan.FromMilliseconds(ms)
                : null,
            Position = TimeSpan.Zero,
            TrackNumber = int.TryParse(vars.GetValueOrDefault("NUMBER"), out var number) && number > 0 ? number : null,
            DiscNumber = int.TryParse(vars.GetValueOrDefault("DISC_NUMBER"), out var disc) && disc > 0 ? disc : null,
            TrackId = vars.GetValueOrDefault("URI") is { Length: > 0 } uri ? uri : vars.GetValueOrDefault("TRACK_ID"),
            ArtworkUrl = LibrespotFields.PickCover(vars.GetValueOrDefault("COVERS")),
        };

        var epoch = Interlocked.Increment(ref _metadataEpoch);
        _metadata = metadata;
        RaiseMetadata(metadata);
        logger.LogInformation("now playing: {Title} — {Artist} ({Album}){Cover}",
            metadata.Title, metadata.Artist, metadata.Album,
            metadata.ArtworkUrl is null ? ", no cover offered" : "");

        if (metadata.ArtworkUrl is { Length: > 0 } coverUrl)
        {
            _ = FetchArtworkAsync(coverUrl, epoch, ct);
        }
    }

    /// <summary>
    /// Download the cover and republish the track with it attached. Dropped
    /// silently if another track started meanwhile (<paramref name="epoch"/>
    /// went stale) — a late arrival must never overwrite the current track's art.
    /// </summary>
    private async Task FetchArtworkAsync(string url, long epoch, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await Http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("cover fetch {Url} returned {Status}", url, (int)response.StatusCode);
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxArtworkBytes)
            {
                logger.LogDebug("cover fetch {Url} ignored ({Bytes} bytes)", url, bytes.Length);
                return;
            }

            if (Interlocked.Read(ref _metadataEpoch) != epoch)
            {
                return;
            }

            var withArt = _metadata with
            {
                Artwork = bytes,
                ArtworkMimeType = response.Content.Headers.ContentType?.MediaType ?? LibrespotFields.SniffImageMime(bytes),
                ArtworkUrl = url,
            };

            // Re-check under the epoch: the track may have changed between the
            // read above and here, in which case _metadata is a different track.
            if (Interlocked.Read(ref _metadataEpoch) != epoch)
            {
                return;
            }

            _metadata = withArt;
            RaiseMetadata(withArt);
            logger.LogInformation("cover art loaded ({Bytes:N0} bytes, {Mime})", bytes.Length, withArt.ArtworkMimeType);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "cover fetch failed for {Url}", url);
        }
    }

    /// <summary>
    /// Refresh the playhead on the current track and republish, so receivers
    /// show real progress. Each republish costs an RTSP round-trip per speaker,
    /// and librespot's position_correction can fire far more often than a
    /// progress bar needs, so a move smaller than a second is not worth telling
    /// anyone about.
    /// </summary>
    private void UpdatePosition(Dictionary<string, string> vars)
    {
        if (!long.TryParse(vars.GetValueOrDefault("POSITION_MS"), out var positionMs) || positionMs < 0)
        {
            return;
        }

        var current = _metadata;
        if (!current.HasContent)
        {
            return;
        }

        var position = TimeSpan.FromMilliseconds(positionMs);
        if (current.Position is { } previous && Math.Abs((position - previous).TotalMilliseconds) < 1000)
        {
            return;
        }

        var updated = current with { Position = position };
        _metadata = updated;
        RaiseMetadata(updated);
    }

    private string WriteEventScript(int port)
    {
        var dir = Path.Combine(Path.GetTempPath(), "streamsemble");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "librespot-event.sh");
        File.WriteAllText(path, $"""
            #!/bin/sh
            # librespot 0.8 exports these on track_changed; COVERS/ARTISTS/
            # ALBUM_ARTISTS are newline-separated lists, which survive
            # --data-urlencode intact.
            curl -s -m 2 -X POST "http://127.0.0.1:{port}/event" \
              --data-urlencode "PLAYER_EVENT=$PLAYER_EVENT" \
              --data-urlencode "NAME=$NAME" \
              --data-urlencode "ARTISTS=$ARTISTS" \
              --data-urlencode "ALBUM_ARTISTS=$ALBUM_ARTISTS" \
              --data-urlencode "ALBUM=$ALBUM" \
              --data-urlencode "COVERS=$COVERS" \
              --data-urlencode "NUMBER=$NUMBER" \
              --data-urlencode "DISC_NUMBER=$DISC_NUMBER" \
              --data-urlencode "ITEM_TYPE=$ITEM_TYPE" \
              --data-urlencode "TRACK_ID=$TRACK_ID" \
              --data-urlencode "URI=$URI" \
              --data-urlencode "DURATION_MS=$DURATION_MS" \
              --data-urlencode "POSITION_MS=$POSITION_MS" \
              --data-urlencode "VOLUME=$VOLUME" >/dev/null 2>&1 || true
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }

    private static int GetFreeTcpPort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
