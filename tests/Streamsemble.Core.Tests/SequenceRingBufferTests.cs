using Streamsemble.Core.Audio;
using Xunit;

namespace Streamsemble.Core.Tests;

public class SequenceRingBufferTests
{
    [Fact]
    public void StoresAndRetrievesBySequence()
    {
        var ring = new SequenceRingBuffer(8);
        ring.Add(5, [1, 2, 3]);

        Assert.True(ring.TryGet(5, out var data));
        Assert.Equal([1, 2, 3], data);
    }

    [Fact]
    public void MissingSequenceIsAMiss()
    {
        var ring = new SequenceRingBuffer(8);
        ring.Add(5, [1]);

        Assert.False(ring.TryGet(6, out _));
    }

    [Fact]
    public void OverwrittenSlotDoesNotAnswerForOldSequence()
    {
        var ring = new SequenceRingBuffer(8);
        ring.Add(1, [1]);
        ring.Add(9, [9]); // same slot (9 % 8 == 1)

        Assert.False(ring.TryGet(1, out _));
        Assert.True(ring.TryGet(9, out var data));
        Assert.Equal([9], data);
    }

    [Fact]
    public void KeepsLastCapacityEntries()
    {
        var ring = new SequenceRingBuffer(4);
        for (var seq = 0; seq < 10; seq++)
        {
            ring.Add(seq, [(byte)seq]);
        }

        for (var seq = 0; seq < 6; seq++)
        {
            Assert.False(ring.TryGet(seq, out _));
        }

        for (var seq = 6; seq < 10; seq++)
        {
            Assert.True(ring.TryGet(seq, out var data));
            Assert.Equal((byte)seq, data[0]);
        }
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var ring = new SequenceRingBuffer(4);
        ring.Add(2, [2]);
        ring.Clear();

        Assert.False(ring.TryGet(2, out _));
    }
}
