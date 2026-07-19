using System.Diagnostics;
using System.Threading.Channels;
using Streamsemble.Core.Audio;

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
public sealed class PacedPcmEmitter(ChannelReader<byte[]> pcm, Action<ReadOnlyMemory<byte>> emit)
{
    private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Opens the render gate (SETRATEANCHORTIME rate=1). Emission holds until this fires.</summary>
    public void Go() => _go.TrySetResult();

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
