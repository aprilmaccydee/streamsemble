using Microsoft.Extensions.Logging;

namespace Streamsemble.Core.Video;

/// <summary>
/// Binds the video source to the video sink for the process lifetime. Simpler
/// than <see cref="Audio.AudioPump"/> because there is nothing to arbitrate:
/// one screen mirrors at a time, so the pump only has to start the sink when a
/// sender appears, forward frames, and stop it when the sender goes away.
///
/// The sink cannot open a stream until the parameter sets are known, so the
/// pump holds frames back until the first codec config arrives — in practice a
/// handful of milliseconds, since senders lead with it.
/// </summary>
public sealed class VideoPump(IVideoSource source, IVideoSink sink, ILogger<VideoPump> logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task _loop = Task.CompletedTask;
    private VideoCodecConfig? _streamConfig;

    public void Start(CancellationToken ct)
    {
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
        _loop = RunAsync(_linked.Token);
        source.ActiveChanged += OnActiveChanged;
    }

    private CancellationTokenSource? _linked;

    private void OnActiveChanged(object? sender, bool active)
    {
        if (active)
        {
            return;
        }

        // Stopping is fire-and-forget from an event handler; the sink's own
        // teardown logs anything that goes wrong.
        _ = StopSinkAsync();
    }

    private async Task StopSinkAsync()
    {
        if (_streamConfig is null)
        {
            return;
        }

        _streamConfig = null;
        try
        {
            await sink.StopStreamAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "video sink teardown failed");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in source.Frames.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var config = source.CodecConfig;
                if (config is null)
                {
                    // Nothing downstream can decode this yet.
                    continue;
                }

                if (_streamConfig is null)
                {
                    _streamConfig = config;
                    logger.LogInformation("video stream starting: {Config}", config.Describe());
                    await sink.StartStreamAsync(config, ct).ConfigureAwait(false);

                    // Opening the output takes seconds (pairing, SETUP, a TCP
                    // connect), and the source has been filling its queue the
                    // whole time — the frame in hand is the oldest of that
                    // backlog. History, but not droppable history: the sender
                    // opened the stream with its one IDR and will not send
                    // another until its config changes, so the backlog holds
                    // the only entry point there is. Replay from the most
                    // recent keyframe — the chain from there is unbroken, so
                    // it decodes cleanly and the sink fast-forwards it to the
                    // live edge. Frames BEFORE that keyframe are the ones a
                    // display would render as corruption (their references are
                    // gone); those are the ones to drop.
                    var backlog = new List<VideoFrame> { frame };
                    while (source.Frames.TryRead(out var queued))
                    {
                        backlog.Add(queued);
                    }

                    var entry = backlog.FindLastIndex(f => f.IsKeyframe);
                    if (entry < 0)
                    {
                        // The opening IDR overflowed the queue; nothing held
                        // now would decode. The display stays dark until the
                        // source produces a fresh keyframe (a config change).
                        logger.LogWarning(
                            "dropped {Count} backlogged frames — no keyframe among them, waiting for the source to send one",
                            backlog.Count);
                        continue;
                    }

                    logger.LogInformation(
                        "replaying {Kept} backlogged frames from the last keyframe ({Dropped} older dropped)",
                        backlog.Count - entry, entry);
                    foreach (var replay in backlog.Skip(entry))
                    {
                        await sink.WriteAsync(replay, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                if (!ReferenceEquals(_streamConfig, config))
                {
                    _streamConfig = config;
                    logger.LogInformation("video stream reconfigured: {Config}", config.Describe());
                    await sink.ReconfigureAsync(config, ct).ConfigureAwait(false);
                }

                await sink.WriteAsync(frame, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "video pump failed");
        }
    }

    /// <summary>
    /// Disposal happens twice in practice — the container owns this and the
    /// hosted service that drives it also tears it down — so it has to be
    /// idempotent rather than throwing on the second pass.
    /// </summary>
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        source.ActiveChanged -= OnActiveChanged;
        _lifetime.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
            // Loop failures are already logged.
        }

        await StopSinkAsync().ConfigureAwait(false);
        _linked?.Dispose();
        _lifetime.Dispose();
    }
}
