using Streamsemble.Core.Metadata;
using Xunit;

namespace Streamsemble.Core.Tests;

public class TrackMetadataTests
{
    [Fact]
    public void ArtworkVersionIsStableAcrossTrackAndProgressUpdates()
    {
        var metadata = new TrackMetadata
        {
            Title = "First track",
            Artwork = [1, 2, 3],
            ArtworkMimeType = "image/jpeg",
        };
        var updated = metadata with
        {
            Title = "Next track on the same album",
            Position = TimeSpan.FromSeconds(30),
            Artwork = metadata.Artwork.ToArray(),
        };

        Assert.NotNull(metadata.ArtworkVersion());
        Assert.Equal(metadata.ArtworkVersion(), updated.ArtworkVersion());
    }

    [Fact]
    public void CorrectedArtworkForTheSameTrackHasANewVersion()
    {
        var metadata = new TrackMetadata { Title = "Track", Artwork = [1, 2, 3] };
        var corrected = metadata with { Artwork = [1, 2, 4] };

        Assert.Equal(metadata.PersistentId(), corrected.PersistentId());
        Assert.NotEqual(metadata.ArtworkVersion(), corrected.ArtworkVersion());
    }

    [Fact]
    public void ArtworkMimeTypeChangesTheVersion()
    {
        var jpeg = new TrackMetadata { Artwork = [1, 2, 3], ArtworkMimeType = "image/jpeg" };
        var png = jpeg with { ArtworkMimeType = "image/png" };

        Assert.NotEqual(jpeg.ArtworkVersion(), png.ArtworkVersion());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("image/jpeg")]
    public void MissingMimeTypeUsesTheJpegDefault(string? mime)
    {
        var metadata = new TrackMetadata { Artwork = [1, 2, 3], ArtworkMimeType = mime };
        var jpeg = metadata with { ArtworkMimeType = "image/jpeg" };

        Assert.Equal(jpeg.ArtworkVersion(), metadata.ArtworkVersion());
    }

    [Fact]
    public void MissingOrClearedArtworkHasNoVersion()
    {
        var empty = new TrackMetadata { Title = "Track", Artwork = [] };

        Assert.Null(empty.ArtworkVersion());
        Assert.Null((empty with { Artwork = null }).ArtworkVersion());
    }
}
