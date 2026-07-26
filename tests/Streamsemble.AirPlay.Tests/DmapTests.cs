using System.Buffers.Binary;
using System.Text;
using Streamsemble.AirPlay.Sender.Raop;
using Streamsemble.Core.Metadata;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class DmapTests
{
    /// <summary>Walk a DMAP payload into (code, value) pairs — what a receiver's parser does.</summary>
    private static List<(string Code, byte[] Value)> Parse(byte[] payload)
    {
        var result = new List<(string, byte[])>();
        var offset = 0;
        while (offset + 8 <= payload.Length)
        {
            var code = Encoding.ASCII.GetString(payload, offset, 4);
            var length = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset + 4));
            Assert.InRange(length, 0, payload.Length - offset - 8);
            result.Add((code, payload[(offset + 8)..(offset + 8 + length)]));
            offset += 8 + length;
        }

        Assert.Equal(payload.Length, offset);
        return result;
    }

    [Fact]
    public void TrackItemIsAWellFormedListingItem()
    {
        var item = Dmap.TrackItem(new TrackMetadata
        {
            Title = "Blue Monday",
            Artist = "New Order",
            Album = "Power, Corruption & Lies",
            AlbumArtist = "New Order",
            Duration = TimeSpan.FromSeconds(450),
            TrackNumber = 3,
            DiscNumber = 1,
            TrackId = "spotify:track:abc",
        });

        var outer = Parse(item);
        var (code, body) = Assert.Single(outer);
        Assert.Equal("mlit", code);

        var tags = Parse(body).ToDictionary(t => t.Code, t => t.Value);
        Assert.Equal("Blue Monday", Encoding.UTF8.GetString(tags["minm"]));
        Assert.Equal("New Order", Encoding.UTF8.GetString(tags["asar"]));
        Assert.Equal("Power, Corruption & Lies", Encoding.UTF8.GetString(tags["asal"]));
        Assert.Equal("New Order", Encoding.UTF8.GetString(tags["asaa"]));

        // Widths are what receivers key off: durations are u32 ms, track and
        // disc numbers u16, the persistent id u64.
        Assert.Equal(450_000u, BinaryPrimitives.ReadUInt32BigEndian(tags["astm"]));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(tags["astn"]));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(tags["asdn"]));
        Assert.Equal(8, tags["mper"].Length);
        Assert.Single(tags["mikd"]);
        Assert.Equal(2, tags["mikd"][0]);
    }

    [Fact]
    public void AbsentFieldsAreOmittedRatherThanSentEmpty()
    {
        var item = Dmap.TrackItem(new TrackMetadata { Title = "Just a title" });
        var tags = Parse(Parse(item)[0].Value).Select(t => t.Code).ToList();

        Assert.Contains("minm", tags);
        Assert.DoesNotContain("asar", tags);
        Assert.DoesNotContain("asal", tags);
        Assert.DoesNotContain("astm", tags);
        Assert.DoesNotContain("astn", tags);
    }

    [Fact]
    public void PersistentIdIsStablePerTrackAndDiffersBetweenTracks()
    {
        var a = new TrackMetadata { TrackId = "spotify:track:aaa", Title = "A" };
        var b = new TrackMetadata { TrackId = "spotify:track:bbb", Title = "A" };

        Assert.Equal(a.PersistentId(), (a with { Position = TimeSpan.FromMinutes(1) }).PersistentId());
        Assert.NotEqual(a.PersistentId(), b.PersistentId());
    }

    [Fact]
    public void PersistentIdFallsBackToTheTitleTripleWhenTheSourceHasNoId()
    {
        var track = new TrackMetadata { Title = "A", Artist = "B", Album = "C" };

        Assert.NotEqual(0UL, track.PersistentId());
        Assert.NotEqual(track.PersistentId(), (track with { Album = "D" }).PersistentId());

        // Empty metadata has no identity at all — nothing to key artwork to.
        Assert.Equal(0UL, new TrackMetadata().PersistentId());
        Assert.Equal(0UL, new TrackMetadata { Position = TimeSpan.FromSeconds(5) }.PersistentId());
    }

    [Fact]
    public void ProgressPlacesTheCurrentRtpTimeAtThePlayhead()
    {
        var metadata = new TrackMetadata
        {
            Duration = TimeSpan.FromSeconds(200),
            Position = TimeSpan.FromSeconds(30),
        };

        var text = Encoding.ASCII.GetString(Dmap.Progress(metadata, currentRtp: 44100 * 100));
        var parts = text.Trim()["progress: ".Length..].Split('/');

        var start = uint.Parse(parts[0]);
        var current = uint.Parse(parts[1]);
        var end = uint.Parse(parts[2]);

        Assert.Equal(44100u * 100, current);
        Assert.Equal(44100u * 70, start);            // 30 s of the track already played
        Assert.Equal(44100u * 270, end);             // start + 200 s duration
    }

    [Fact]
    public void ProgressWrapsWithoutOverflowingWhenTheTimelineIsNearU32Max()
    {
        // RTP time is a free-running u32; a track whose start sits before a
        // wrap must still produce a parseable triple rather than throw.
        var metadata = new TrackMetadata
        {
            Duration = TimeSpan.FromSeconds(200),
            Position = TimeSpan.FromSeconds(30),
        };

        var text = Encoding.ASCII.GetString(Dmap.Progress(metadata, currentRtp: 1000));
        var parts = text.Trim()["progress: ".Length..].Split('/');

        Assert.Equal(unchecked((uint)(1000 - 44100 * 30)), uint.Parse(parts[0]));
        Assert.Equal(1000u, uint.Parse(parts[1]));
    }

    [Fact]
    public void ProgressWithoutDurationStillReportsAPlayableWindow()
    {
        var text = Encoding.ASCII.GetString(Dmap.Progress(new TrackMetadata { Title = "Live" }, currentRtp: 44100 * 10));
        var parts = text.Trim()["progress: ".Length..].Split('/');

        var start = uint.Parse(parts[0]);
        var current = uint.Parse(parts[1]);
        var end = uint.Parse(parts[2]);

        Assert.True(start < current, "playhead must sit inside the window");
        Assert.True(current < end, "playhead must sit inside the window");
    }
}
