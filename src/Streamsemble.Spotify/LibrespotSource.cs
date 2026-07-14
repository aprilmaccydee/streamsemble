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
    private TrackMetadata _metadata = new();

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
                HandleEvent(form.AllKeys.OfType<string>().ToDictionary(k => k, k => form[k] ?? ""));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bad librespot event callback");
            }
        }
    }

    private void HandleEvent(Dictionary<string, string> vars)
    {
        var playerEvent = vars.GetValueOrDefault("PLAYER_EVENT", "");
        logger.LogDebug("librespot event {Event}", playerEvent);
        switch (playerEvent)
        {
            case "playing":
                SetState(Core.Abstractions.SourceState.Active);
                break;
            case "paused":
            case "loading":
                if (State == Core.Abstractions.SourceState.Active)
                {
                    SetState(Core.Abstractions.SourceState.Paused);
                }

                break;
            case "stopped":
            case "session_disconnected":
                SetState(Core.Abstractions.SourceState.Idle);
                break;
            case "track_changed":
                _metadata = new TrackMetadata
                {
                    Title = vars.GetValueOrDefault("NAME"),
                    Artist = vars.GetValueOrDefault("ARTISTS")?.Replace('\n', ','),
                    Album = vars.GetValueOrDefault("ALBUM"),
                    Duration = long.TryParse(vars.GetValueOrDefault("DURATION_MS"), out var ms)
                        ? TimeSpan.FromMilliseconds(ms)
                        : null,
                };
                RaiseMetadata(_metadata);
                break;
            case "volume_changed":
                if (ushort.TryParse(vars.GetValueOrDefault("VOLUME"), out var volume))
                {
                    RaiseVolume(volume / 65535f);
                }

                break;
        }
    }

    private string WriteEventScript(int port)
    {
        var dir = Path.Combine(Path.GetTempPath(), "streamsemble");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "librespot-event.sh");
        File.WriteAllText(path, $"""
            #!/bin/sh
            curl -s -m 2 -X POST "http://127.0.0.1:{port}/event" \
              --data-urlencode "PLAYER_EVENT=$PLAYER_EVENT" \
              --data-urlencode "NAME=$NAME" \
              --data-urlencode "ARTISTS=$ARTISTS" \
              --data-urlencode "ALBUM=$ALBUM" \
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
