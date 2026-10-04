using Streamsemble.AirPlay.Receiver;
using Streamsemble.Core.Audio;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class ReceiverPcmTests
{
    [Fact]
    public void CanonicalInputKeepsItsBytesAndRenderStamp()
    {
        var source = new AirPlayReceiverSource();
        var first = Samples(352, 1);
        var second = Samples(352, 2);

        source.PushDecodedPcm(first, 5_000_000_000);
        source.PushDecodedPcm(second, 5_008_500_000);

        var frames = Drain(source);
        Assert.Equal(2, frames.Count);
        Assert.Equal(first, frames[0].Data.ToArray());
        Assert.Equal(second, frames[1].Data.ToArray());
        Assert.Equal(5_000_000_000, frames[0].TargetNanos);
        Assert.Equal(5_008_500_000, frames[1].TargetNanos);
        Assert.Equal(352, frames[1].Timestamp);
    }

    [Fact]
    public void AacBlocksAndConcealedGapPreserveCanonicalSampleTimeline()
    {
        const long anchorNanos = 5_000_000_000_000;
        var source = new AirPlayReceiverSource();
        var expected = new List<byte>();

        // Eleven AAC access units equal exactly 32 canonical frames. A lost
        // AAC unit is concealed in scheduler-sized 352/352/320 portions.
        for (var unit = 0; unit < 11; unit++)
        {
            var block = Samples(1024, unit == 4 ? (byte)0 : (byte)(unit + 1));
            expected.AddRange(block);
            if (unit == 4)
            {
                PushPart(0, 352);
                PushPart(352, 352);
                PushPart(704, 320);
            }
            else
            {
                PushPart(0, 1024);
            }

            void PushPart(int offset, int count)
            {
                var target = anchorNanos + (unit * 1024L + offset) * 1_000_000_000L / 44100;
                source.PushDecodedPcm(block.AsMemory(offset * 4, count * 4), target);
            }
        }

        var frames = Drain(source);
        Assert.Equal(32, frames.Count);
        Assert.All(frames, frame => Assert.Equal(PcmFrame.CanonicalFrameBytes, frame.Data.Length));
        Assert.Equal(expected.ToArray(), frames.SelectMany(frame => frame.Data.ToArray()).ToArray());
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(i * 352L, frames[i].Timestamp);
            var expectedTarget = anchorNanos + i * 352L * 1_000_000_000L / 44100;
            // Incoming integer stamps have already rounded the unit's offset;
            // adding the within-unit offset can round once more, at most 1 ns.
            Assert.InRange(frames[i].TargetNanos, expectedTarget - 1, expectedTarget);
        }
    }

    [Fact]
    public void PartialTailWaitsForEnoughSamplesAndUnstampedAudioStaysUnstamped()
    {
        var source = new AirPlayReceiverSource();
        source.PushDecodedPcm(Samples(1024, 7));
        var first = Drain(source);
        Assert.Equal(2, first.Count);

        source.PushDecodedPcm(Samples(32, 9));
        var last = Assert.Single(Drain(source));
        Assert.Equal(704, last.Timestamp);
        Assert.Equal(Samples(320, 7).Concat(Samples(32, 9)).ToArray(), last.Data.ToArray());
        Assert.All(first, frame => Assert.Equal(0, frame.TargetNanos));
        Assert.Equal(0, last.TargetNanos);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("idle")]
    [InlineData("stop")]
    public async Task StateResetDiscardsOnlyTheOldPartialTail(string transition)
    {
        var source = new AirPlayReceiverSource();
        source.PushDecodedPcm(Samples(452, 1), 5_000_000_000);
        Assert.Single(Drain(source));

        switch (transition)
        {
            case "pause": source.MarkPaused(); break;
            case "idle": source.MarkIdle(); break;
            case "stop": await source.StopAsync(); break;
        }

        source.PushDecodedPcm(Samples(352, 2), 9_000_000_000);

        var frame = Assert.Single(Drain(source));
        Assert.Equal(Samples(352, 2), frame.Data.ToArray());
        Assert.Equal(352, frame.Timestamp);
        Assert.Equal(9_000_000_000, frame.TargetNanos);
    }

    private static byte[] Samples(int count, byte value)
    {
        var pcm = new byte[count * AudioFormat.Canonical.BlockAlign];
        Array.Fill(pcm, value);
        return pcm;
    }

    private static List<PcmFrame> Drain(AirPlayReceiverSource source)
    {
        var frames = new List<PcmFrame>();
        while (source.Frames.TryRead(out var frame))
        {
            frames.Add(frame);
        }

        return frames;
    }
}
