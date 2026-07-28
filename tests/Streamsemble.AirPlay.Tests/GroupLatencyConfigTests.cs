using Streamsemble.AirPlay.Sender.AirPlay2;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class GroupLatencyConfigTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetKeepsTheDefault(string? raw)
    {
        var (seconds, overridden, warning) = AirPlay2Session.ParseGroupLatency(raw);

        // 1.5 s: verified band for the speakers AND under the ~1.75 s
        // realtime inbound transmission lead the receiver renders against.
        Assert.Equal(1.5, seconds);
        Assert.False(overridden);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("1.0", 1.0)]
    [InlineData("2", 2.0)]
    public void ValuesInsideTheVerifiedBandAreTakenQuietly(string raw, double expected)
    {
        var (seconds, overridden, warning) = AirPlay2Session.ParseGroupLatency(raw);

        Assert.Equal(expected, seconds);
        Assert.True(overridden);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("0.8")]
    [InlineData("3.0")]
    public void ValuesOutsideTheVerifiedBandAreAcceptedButFlagged(string raw)
    {
        var (_, overridden, warning) = AirPlay2Session.ParseGroupLatency(raw);

        Assert.True(overridden);
        Assert.NotNull(warning);
        Assert.Contains("verified", warning);
    }

    [Theory]
    [InlineData("0.1", 0.5)]
    [InlineData("60", 5.0)]
    [InlineData("-3", 0.5)]
    public void AbsurdValuesAreClampedRatherThanObeyed(string raw, double expected)
    {
        var (seconds, overridden, warning) = AirPlay2Session.ParseGroupLatency(raw);

        Assert.Equal(expected, seconds);
        Assert.True(overridden);
        Assert.Contains("clamped", warning);
    }

    [Fact]
    public void GarbageFallsBackToTheDefaultAndSaysSo()
    {
        var (seconds, overridden, warning) = AirPlay2Session.ParseGroupLatency("two seconds");

        Assert.Equal(1.5, seconds);
        Assert.False(overridden);
        Assert.Contains("not a number", warning);
    }

    [Fact]
    public void SamplesTrackTheConfiguredSeconds()
    {
        // The SETUP plist and the anchor both derive from this, so they can
        // never disagree with the seconds value.
        Assert.Equal((int)(AirPlay2Session.GroupPresentationLatencySeconds * 44100),
            AirPlay2Session.GroupPresentationLatencySamples);
    }
}
