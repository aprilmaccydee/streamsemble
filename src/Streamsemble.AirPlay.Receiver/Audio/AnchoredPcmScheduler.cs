using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Streamsemble.Timing.Ptp;

namespace Streamsemble.AirPlay.Receiver.Audio;

/// <summary>
/// Emits realtime PCM so it turns AUDIBLE at its anchored render time.
/// Modern macOS realtime ("buffered realtime") transmits ~1.75 s ahead of
/// presentation, so arrival order is the wrong clock — rendering on
/// arrival runs early by whatever lead the engine uses, differently every
/// session. The 0xD7 anchors state "frame F is audible at local time T"
/// (already clock-translated, refreshed ~1/s); frame N must be audible at
/// T + (N − F)/44100, and since everything emitted here takes the hub's
/// group presentation latency to reach the speakers, it is emitted
/// <paramref name="leadNanos"/> (that group latency) EARLY — the TV
/// contract: declare zero, present at the anchor. That only works while
/// the hub latency fits inside the sender's transmission lead; if it
/// doesn't, frames emit on arrival and trail by the difference, loudly.
/// Frames arriving before any anchor wait up to a second for one; a sender
/// that never sends 0xD7 falls back to on-arrival, loudly.
/// </summary>
public sealed class AnchoredPcmScheduler(
    Action<ReadOnlyMemory<byte>> emit,
    ILogger logger,
    long leadNanos = 0,
    Func<long>? clockNanos = null)
{
    private sealed record Anchor(uint Frame, long Nanos);

    private readonly Channel<(uint Rtp, byte[] Pcm)> _frames = Channel.CreateUnbounded<(uint, byte[])>();
    private readonly Func<long> _now = clockNanos ?? (() => PtpReceiverClock.NowNanos);
    private Anchor? _anchor;
    private bool _fallback;
    private bool _budgetWarned;

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
                var target = anchor.Nanos + unchecked((int)(rtp - anchor.Frame)) * 1_000_000_000L / 44100 - leadNanos;
                var aheadNs = target - _now();
                if (aheadNs <= 1_000_000)
                {
                    if (aheadNs < -250_000_000 && !_budgetWarned)
                    {
                        _budgetWarned = true;
                        logger.LogWarning(
                            "anchored frame is {LateMs:F0} ms past its emit time — the hub group latency exceeds "
                            + "the sender's transmission lead; audio will trail by about this much "
                            + "(lower STREAMSEMBLE_GROUP_LATENCY)",
                            -aheadNs / 1e6);
                    }

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
