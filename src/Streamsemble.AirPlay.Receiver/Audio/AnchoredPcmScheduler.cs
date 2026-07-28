using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Streamsemble.Timing.Ptp;

namespace Streamsemble.AirPlay.Receiver.Audio;

/// <summary>
/// Emits realtime PCM at its anchored render time instead of on arrival.
/// Modern macOS realtime ("buffered realtime") transmits with a variable
/// lead — roughly 1.75 s of frames arrive before they are due — so
/// on-arrival rendering runs early by whatever lead the engine happens to
/// use that session, and lip sync lands differently every connect. The
/// control channel's 0xD7 packets state "frame F is audible at grandmaster
/// time T" (refreshed ~1/s); each frame N is held until
/// T + (N − F)/44100, and the hub's group latency — the same figure we
/// declare in /info audioLatencies — carries it from there to the speakers
/// on schedule. Frames arriving before any anchor wait up to a second for
/// one; a sender that never sends 0xD7 falls back to on-arrival, loudly.
/// </summary>
public sealed class AnchoredPcmScheduler(
    Action<ReadOnlyMemory<byte>> emit,
    ILogger logger,
    Func<long>? clockNanos = null)
{
    private sealed record Anchor(uint Frame, long Nanos);

    private readonly Channel<(uint Rtp, byte[] Pcm)> _frames = Channel.CreateUnbounded<(uint, byte[])>();
    private readonly Func<long> _now = clockNanos ?? (() => PtpReceiverClock.NowNanos);
    private Anchor? _anchor;
    private bool _fallback;

    /// <summary>Latest 0xD7 mapping: frame is audible at the grandmaster reading.</summary>
    public void SetAnchor(uint frame, long nanos) => Volatile.Write(ref _anchor, new Anchor(frame, nanos));

    public bool HasAnchor => Volatile.Read(ref _anchor) is not null;

    public void Enqueue(uint rtp, byte[] pcm) => _frames.Writer.TryWrite((rtp, pcm));

    /// <summary>Sender flush: drop everything queued but keep the anchor mapping.</summary>
    public void Flush()
    {
        while (_frames.Reader.TryRead(out _))
        {
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        long firstFrameAt = 0;
        await foreach (var (rtp, pcm) in _frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var anchor = Volatile.Read(ref _anchor);
            if (anchor is null && !_fallback)
            {
                firstFrameAt = firstFrameAt == 0 ? _now() : firstFrameAt;
                while ((anchor = Volatile.Read(ref _anchor)) is null && _now() - firstFrameAt < 1_000_000_000)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }

                _fallback = anchor is null;
                if (_fallback)
                {
                    logger.LogWarning("no 0xD7 anchor within 1 s of first audio — rendering on arrival (lip sync unanchored)");
                }
            }

            while (anchor is not null)
            {
                var target = anchor.Nanos + unchecked((int)(rtp - anchor.Frame)) * 1_000_000_000L / 44100;
                var aheadNs = target - _now();
                if (aheadNs <= 1_000_000)
                {
                    break;
                }

                // Chunked so a fresh anchor (drift correction) takes effect.
                await Task.Delay((int)Math.Min(aheadNs / 1_000_000, 250), ct).ConfigureAwait(false);
                anchor = Volatile.Read(ref _anchor);
            }

            emit(pcm);
        }
    }
}
