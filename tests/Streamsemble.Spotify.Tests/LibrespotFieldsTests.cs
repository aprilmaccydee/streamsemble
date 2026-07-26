using Streamsemble.Spotify;
using Xunit;

namespace Streamsemble.Spotify.Tests;

public class LibrespotFieldsTests
{
    private const string Cover640 = "https://i.scdn.co/image/ab67616d0000b273abcdef0123456789abcdef01";
    private const string Cover300 = "https://i.scdn.co/image/ab67616d00001e02abcdef0123456789abcdef01";
    private const string Cover64 = "https://i.scdn.co/image/ab67616d00004851abcdef0123456789abcdef01";

    [Fact]
    public void PicksTheLargestCoverRegardlessOfListOrder()
    {
        // librespot does not promise a size order, so the small one arriving
        // first must not win.
        Assert.Equal(Cover640, LibrespotFields.PickCover($"{Cover64}\n{Cover300}\n{Cover640}"));
        Assert.Equal(Cover640, LibrespotFields.PickCover($"{Cover640}\n{Cover300}\n{Cover64}"));
        Assert.Equal(Cover300, LibrespotFields.PickCover($"{Cover64}\n{Cover300}"));
    }

    [Fact]
    public void UnrecognisedCoverUrlsKeepLibrespotsOrder()
    {
        // A CDN scheme change must degrade to "take the first", not to null.
        Assert.Equal("https://example.test/a.jpg",
            LibrespotFields.PickCover("https://example.test/a.jpg\nhttps://example.test/b.jpg"));
    }

    [Fact]
    public void AKnownSizeStillBeatsAnUnrecognisedUrl()
    {
        Assert.Equal(Cover640, LibrespotFields.PickCover($"https://example.test/a.jpg\n{Cover640}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void NoCoversMeansNoUrl(string? covers) => Assert.Null(LibrespotFields.PickCover(covers));

    [Fact]
    public void MultipleArtistsBecomeOneDisplayLine()
    {
        Assert.Equal("Fatboy Slim, Bootsy Collins", LibrespotFields.JoinList("Fatboy Slim\nBootsy Collins"));
        Assert.Equal("New Order", LibrespotFields.JoinList("New Order"));
        Assert.Null(LibrespotFields.JoinList(""));
        Assert.Null(LibrespotFields.JoinList(null));
    }

    [Fact]
    public void BlankEntriesInAListAreDropped()
    {
        Assert.Equal("A, B", LibrespotFields.JoinList("A\n\nB\n"));
    }

    [Fact]
    public void ImageSniffingFallsBackToJpeg()
    {
        Assert.Equal("image/png", LibrespotFields.SniffImageMime([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Equal("image/jpeg", LibrespotFields.SniffImageMime([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0]));
        Assert.Equal("image/jpeg", LibrespotFields.SniffImageMime([]));
    }
}
