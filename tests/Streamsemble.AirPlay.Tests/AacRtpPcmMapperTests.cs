using Microsoft.Extensions.Logging.Abstractions;
using Streamsemble.AirPlay.Receiver.Audio;
using Streamsemble.Core.Audio;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class AacRtpPcmMapperTests
{
    private const int AacSamples = AacDecoderPipe.SamplesPerFrame;
    private const int AacBytes = AacSamples * 4;

    [Fact]
    public void ArbitraryDecoderChunksKeepEachPacketsOriginalRtpAndPcm()
    {
        var output = new List<(uint Rtp, byte[] Pcm)>();
        var mapper = new AacRtpPcmMapper((rtp, pcm) => output.Add((rtp, pcm)), output.Clear);
        Assert.True(mapper.TryQueuePacket(0));
        Assert.True(mapper.TryQueuePacket(1024));
        Assert.True(mapper.TryQueuePacket(3072)); // RTP 2048 was lost before decoding.
        var decoded = new byte[AacBytes * 3];
        DecodedBlock(1).CopyTo(decoded, 0);
        DecodedBlock(2).CopyTo(decoded, AacBytes);
        DecodedBlock(3).CopyTo(decoded, AacBytes * 2);

        // Deliberately split inside a stereo sample and span AAC boundaries.
        mapper.WriteDecodedPcm(decoded.AsSpan(0, 1));
        mapper.WriteDecodedPcm(decoded.AsSpan(1, 8192));
        mapper.WriteDecodedPcm(decoded.AsSpan(8193));

        Assert.Equal(new uint[] { 0, 352, 704, 1024, 1376, 1728, 3072, 3424, 3776 },
            output.Select(frame => frame.Rtp));
        Assert.Equal(decoded, output.SelectMany(frame => frame.Pcm).ToArray());
        Assert.All(output, frame => Assert.InRange(frame.Pcm.Length, 4, PcmFrame.CanonicalFrameBytes));
    }

    [Fact]
    public void WraparoundAndLateDuplicatesDoNotConsumeDecoderTimestampSlots()
    {
        var output = new List<uint>();
        var mapper = new AacRtpPcmMapper((rtp, _) => output.Add(rtp), output.Clear);
        const uint first = uint.MaxValue - 1023;
        Assert.True(mapper.TryQueuePacket(first));
        Assert.True(mapper.TryQueuePacket(0));
        Assert.False(mapper.TryQueuePacket(first));
        Assert.False(mapper.TryQueuePacket(0));
        Assert.True(mapper.TryQueuePacket(2048)); // Skip 1024, then reject its late arrival.
        Assert.False(mapper.TryQueuePacket(1024));

        mapper.WriteDecodedPcm(new byte[AacBytes * 3]);

        Assert.Equal(new uint[] { first, first + 352, first + 704, 0, 352, 704, 2048, 2400, 2752 }, output);
    }

    [Fact]
    public void FlushDiscardsOldOutputWithoutShiftingAPartiallyDecodedBlock()
    {
        var output = new List<(uint Rtp, byte[] Pcm)>();
        var flushes = 0;
        var mapper = new AacRtpPcmMapper((rtp, pcm) => output.Add((rtp, pcm)), () =>
        {
            flushes++;
            output.Clear();
        });
        mapper.TryQueuePacket(0);
        mapper.TryQueuePacket(1024);
        var oldPcm = Enumerable.Repeat((byte)1, AacBytes * 2).ToArray();
        mapper.WriteDecodedPcm(oldPcm.AsSpan(0, AacBytes + 317));
        Assert.NotEmpty(output);

        mapper.Flush();
        Assert.True(mapper.TryQueuePacket(0)); // A seek may restart the RTP cursor.
        mapper.WriteDecodedPcm(oldPcm.AsSpan(AacBytes + 317));
        Assert.Empty(output);
        mapper.WriteDecodedPcm(DecodedBlock(9));

        Assert.Equal(1, flushes);
        Assert.Equal(new uint[] { 0, 352, 704 }, output.Select(frame => frame.Rtp));
        Assert.All(output.SelectMany(frame => frame.Pcm), value => Assert.Equal((byte)9, value));
    }

    [Fact]
    public async Task SparseAacLossConcealsElevenSecondsWithoutCompressingTheTimeline()
    {
        const int packetCount = 47_602;
        const long anchorNanos = 5_000_000_000_000;
        long emittedSamples = 0;
        long silenceSamples = 0;
        var mismatchedTargets = 0;
        var scheduler = new AnchoredPcmScheduler((pcm, target) =>
        {
            if (target != anchorNanos + emittedSamples * 1_000_000_000L / 44100)
            {
                mismatchedTargets++;
            }

            var samples = pcm.Length / AudioFormat.Canonical.BlockAlign;
            emittedSamples += samples;
            if (pcm.Span[0] == 0)
            {
                silenceSamples += samples;
            }
        }, NullLogger.Instance, clockNanos: () => anchorNanos + 2_000_000_000_000);
        scheduler.SetAnchor(0, anchorNanos);
        var mapper = new AacRtpPcmMapper(scheduler.Enqueue, scheduler.Flush);
        var decoded = DecodedBlock(7);
        var missingPackets = 0;
        for (var i = 0; i < packetCount; i++)
        {
            if (i > 0 && i % 100 == 0)
            {
                missingPackets++;
                continue;
            }

            Assert.True(mapper.TryQueuePacket((uint)(i * AacSamples)));
            mapper.WriteDecodedPcm(decoded);
            if (i % 128 == 0)
            {
                // Drain periodically so this virtual-duration test has a
                // bounded working set rather than storing all decoded PCM.
                await DrainAsync(scheduler);
            }
        }

        await DrainAsync(scheduler);
        Assert.Equal(476, missingPackets);
        Assert.Equal((long)packetCount * AacSamples, emittedSamples);
        Assert.Equal((long)missingPackets * AacSamples, silenceSamples);
        Assert.Equal(silenceSamples, scheduler.ConcealedSamples);
        Assert.InRange(silenceSamples / 44100.0, 11.05, 11.06);
        Assert.Equal(0, mismatchedTargets);
    }

    private static byte[] DecodedBlock(byte marker) => Enumerable.Repeat(marker, AacBytes).ToArray();

    private static async Task DrainAsync(AnchoredPcmScheduler scheduler)
    {
        // All virtual timestamps are due: the loop drains synchronously and
        // then waits for its next channel item, where cancellation ends it.
        using var cts = new CancellationTokenSource();
        var run = scheduler.RunAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
