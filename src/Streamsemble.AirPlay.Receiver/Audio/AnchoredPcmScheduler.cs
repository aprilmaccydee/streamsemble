using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Streamsemble.Core.Audio;
using Streamsemble.Timing.Ptp;

namespace Streamsemble.AirPlay.Receiver.Audio;

/// <summary>
/// Emits realtime PCM stamped with its anchored render deadline. Modern
/// macOS realtime ("buffered realtime") transmits ~1.75 s ahead of
/// presentation, so arrival order is the wrong clock. The 0xD7 anchors
/// state "frame F is audible at local time T" (already clock-translated,
/// refreshed ~1/s); frame N is audible at T + (N − F)/44100. Each frame is
/// handed to <paramref name="emit"/> with that absolute target time —
/// downstream, the sink derives its whole send timeline from the stamp,
/// so presentation is exact with no estimated pipeline constants — and
/// emission is paced <paramref name="leadNanos"/> (the group latency plus
/// scheduling slack) ahead of the target so the data is always downstream
/// in time. Frames arriving before any anchor wait up to a second for
/// one; a sender that never sends 0xD7 falls back to on-arrival with
/// unstamped frames (target 0), loudly.
/// </summary>
public sealed class AnchoredPcmScheduler(
    Action<ReadOnlyMemory<byte>, long> emit,
    ILogger logger,
    long leadNanos = 0,
    Func<long>? clockNanos = null)
{
    // Ordinary packet loss must occupy time in the outgoing sample clock.
    // Bound concealment so a seek or a new RTP epoch cannot enqueue hours
    // of silence before its first real frame.
    internal const int MaxConcealmentSamples = 2 * 44100;

    private sealed record Anchor(uint Frame, long Nanos);

    private readonly object _enqueueGate = new();
    private readonly Channel<(uint Rtp, byte[] Pcm)> _frames = Channel.CreateUnbounded<(uint, byte[])>();
    private readonly Func<long> _now = clockNanos ?? (() => PtpReceiverClock.NowNanos);
    private Anchor? _anchor;
    private bool _fallback;
    private bool _budgetWarned;
    private uint? _nextRtp;
    private long _concealedSamples;
    private long _nextConcealmentLogSamples = 1;

    /// <summary>Latest 0xD7 mapping: frame is audible at the grandmaster reading.</summary>
    public void SetAnchor(uint frame, long nanos) => Volatile.Write(ref _anchor, new Anchor(frame, nanos));

    public bool HasAnchor => Volatile.Read(ref _anchor) is not null;

    public void Enqueue(uint rtp, byte[] pcm)
    {
        var blockAlign = AudioFormat.Canonical.BlockAlign;
        var samples = pcm.Length / blockAlign;
        if (samples == 0)
        {
            return;
        }

        lock (_enqueueGate)
        {
            if (_nextRtp is { } next)
            {
                // Signed subtraction follows the RTP cursor across uint wrap.
                var gap = unchecked((int)(rtp - next));
                if (gap < 0)
                {
                    var overlap = -(long)gap;
                    if (overlap >= samples)
                    {
                        return; // duplicate or an already-concealed late packet
                    }

                    pcm = pcm.AsSpan((int)overlap * blockAlign).ToArray();
                    samples -= (int)overlap;
                    rtp = next;
                }
                else if (gap <= MaxConcealmentSamples)
                {
                    // Simply omitting a lost packet compresses the source's
                    // running sample counter. Buffered AAC receivers keep
                    // their original anchor, so each omission permanently
                    // consumes their render-head lead until they go silent.
                    _concealedSamples += gap;
                    if (_concealedSamples >= _nextConcealmentLogSamples)
                    {
                        logger.LogInformation(
                            "realtime loss concealment: inserted {Samples} samples ({Ms:F0} ms) of silence to preserve the audio timeline",
                            _concealedSamples, _concealedSamples * 1000.0 / AudioFormat.Canonical.SampleRate);
                        _nextConcealmentLogSamples = (_concealedSamples / AudioFormat.Canonical.SampleRate + 1)
                            * AudioFormat.Canonical.SampleRate;
                    }

                    while (gap > 0)
                    {
                        var missing = Math.Min(gap, PcmFrame.SamplesPerFrame);
                        _frames.Writer.TryWrite((next, new byte[missing * blockAlign]));
                        next = unchecked(next + (uint)missing);
                        gap -= missing;
                    }
                }
                else
                {
                    logger.LogWarning(
                        "realtime RTP discontinuity of {GapMs:F0} ms exceeds concealment limit; restarting receive cursor",
                        gap * 1000.0 / AudioFormat.Canonical.SampleRate);
                }
            }

            _frames.Writer.TryWrite((rtp, pcm));
            _nextRtp = unchecked(rtp + (uint)samples);
        }
    }

    /// <summary>Sender flush: drop everything queued but keep the anchor mapping.</summary>
    public void Flush()
    {
        lock (_enqueueGate)
        {
            while (_frames.Reader.TryRead(out _))
            {
            }

            _nextRtp = null;
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

            long audibleAt = 0;
            while (anchor is not null)
            {
                audibleAt = anchor.Nanos + unchecked((int)(rtp - anchor.Frame)) * 1_000_000_000L / 44100;
                var aheadNs = audibleAt - leadNanos - _now();
                if (aheadNs <= 1_000_000)
                {
                    if (aheadNs < -250_000_000 && !_budgetWarned)
                    {
                        _budgetWarned = true;
                        logger.LogWarning(
                            "anchored frame is {LateMs:F0} ms past its emit time — the hub group latency exceeds "
                            + "the sender's transmission lead; the whole timeline (audio, picture, lights) trails "
                            + "uniformly by about this much (lower STREAMSEMBLE_GROUP_LATENCY to shrink it)",
                            -aheadNs / 1e6);
                    }

                    break;
                }

                // Chunked so a fresh anchor (drift correction) takes effect.
                await Task.Delay((int)Math.Min(aheadNs / 1_000_000, 250), ct).ConfigureAwait(false);
                anchor = Volatile.Read(ref _anchor);
            }

            emit(pcm, audibleAt);
        }
    }
}
