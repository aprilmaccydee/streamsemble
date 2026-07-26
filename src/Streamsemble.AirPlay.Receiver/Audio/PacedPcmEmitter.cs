using System.Diagnostics;
using System.Threading.Channels;
using Streamsemble.Core.Audio;
using Streamsemble.Timing.Ptp;

namespace Streamsemble.AirPlay.Receiver.Audio;

/// <summary>
/// Re-chunks decoded PCM into canonical 352-sample frames and pushes them to
/// the receiver source at real-time rate. Buffered senders burst seconds of
/// audio up front (that's the point of the mode); the pipeline's bounded
/// frame channel would drop the burst, so the excess waits here — and via the
/// decoder channel's backpressure, ultimately in the sender's TCP window,
/// exactly where a real receiver keeps it. Same pacing scheme as
/// LibrespotSource: ≤12 ms ahead of a stopwatch, re-anchor after long gaps.
/// </summary>
public sealed class PacedPcmEmitter(
    ChannelReader<byte[]> pcm,
    Action<ReadOnlyMemory<byte>> emit,
    Func<long>? clockNanos = null)
{
    private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<long> _now = clockNanos ?? (() => PtpReceiverClock.NowNanos);
    private long _gateNanos;
    private int _gateArmed;

    /// <summary>Opens the render gate immediately (no anchor time to honor).</summary>
    public void Go() => GoAt(0);

    /// <summary>
    /// Opens — or, mid-stream, re-arms — the render gate at an absolute
    /// grandmaster clock reading. SETRATEANCHORTIME rate=1 means "this frame
    /// renders at network time N", and the sender delays its own video to N
    /// plus the latency we declare; emitting on request-arrival instead of at
    /// N plays the whole anchor lead early. Assumes the next queued frame IS
    /// the anchored one — true at stream start and after FLUSHBUFFERED, the
    /// only places senders re-anchor. A gate already in the past opens
    /// immediately.
    /// </summary>
    public void GoAt(long clockNanos)
    {
        Interlocked.Exchange(ref _gateNanos, clockNanos);
        Interlocked.Exchange(ref _gateArmed, 1);
        _go.TrySetResult();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await _go.Task.WaitAsync(ct).ConfigureAwait(false);

        var frameBytes = PcmFrame.CanonicalFrameBytes;
        var pending = new List<byte>(frameBytes * 4);
        var stopwatch = Stopwatch.StartNew();
        long pacedSamples = 0;

        await foreach (var chunk in pcm.ReadAllAsync(ct).ConfigureAwait(false))
        {
            pending.AddRange(chunk);
            var offset = 0;
            while (pending.Count - offset >= frameBytes)
            {
                if (Interlocked.Exchange(ref _gateArmed, 0) == 1)
                {
                    // Chunked so a re-anchor during the hold takes effect.
                    while (Volatile.Read(ref _gateNanos) - _now() > 1_000_000)
                    {
                        var waitMs = (Volatile.Read(ref _gateNanos) - _now()) / 1_000_000;
                        await Task.Delay((int)Math.Min(waitMs, 250), ct).ConfigureAwait(false);
                    }

                    stopwatch.Restart();
                    pacedSamples = 0;
                }

                var frame = new byte[frameBytes];
                pending.CopyTo(offset, frame, 0, frameBytes);
                offset += frameBytes;
                emit(frame);

                pacedSamples += PcmFrame.SamplesPerFrame;
                var targetMs = pacedSamples * 1000.0 / Core.Audio.AudioFormat.Canonical.SampleRate;
                var aheadMs = targetMs - stopwatch.Elapsed.TotalMilliseconds;
                if (aheadMs > 12)
                {
                    await Task.Delay((int)aheadMs, ct).ConfigureAwait(false);
                }
                else if (aheadMs < -1000)
                {
                    // A pause/flush left us far behind; re-anchor instead of bursting.
                    stopwatch.Restart();
                    pacedSamples = 0;
                }
            }

            pending.RemoveRange(0, offset);
        }
    }
}
