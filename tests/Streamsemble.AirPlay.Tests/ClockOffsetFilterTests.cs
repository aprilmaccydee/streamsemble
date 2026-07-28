using Streamsemble.AirPlay.Receiver.Audio;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class ClockOffsetFilterTests
{
    [Fact]
    public void ConvergesOnTheMinimumObservedOffset()
    {
        var filter = new ClockOffsetFilter();

        // True offset 1_000_000; samples carry varying network delay on top.
        Assert.Equal(1_000_500, filter.Update(1_000_500));
        Assert.Equal(1_000_200, filter.Update(1_000_200));
        Assert.Equal(1_000_200, filter.Update(1_003_000));
        Assert.Equal(1_000_050, filter.Update(1_000_050));
        Assert.Equal(1_000_050, filter.Update(1_001_000));
    }

    [Fact]
    public void WindowSlidesSoDriftKeepsBeingTracked()
    {
        var filter = new ClockOffsetFilter(window: 4);

        filter.Update(100);
        filter.Update(500);
        filter.Update(500);
        filter.Update(500);

        // The 100 falls out of the 4-sample window on the next update: the
        // min follows the newer (drifted) readings instead of pinning to a
        // stale minimum forever.
        Assert.Equal(300, filter.Update(300));
    }

    [Fact]
    public void HandlesTheRealScale()
    {
        // Sender clock ~26 h since boot vs local Unix nanos: offsets around
        // 1.78e18 must survive the arithmetic.
        var filter = new ClockOffsetFilter();
        var offset = 1_785_154_000_000_000_000L;
        Assert.Equal(offset, filter.Update(offset + 400_000) - 400_000);
    }
}
