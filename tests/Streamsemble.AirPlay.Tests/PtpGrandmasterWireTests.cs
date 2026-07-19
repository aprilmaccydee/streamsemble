using System.Buffers.Binary;
using Streamsemble.Timing.Ptp;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

/// <summary>
/// Pins the grandmaster (receiver-role) PTP builders to the wire shapes a real
/// Mac slaved to in debug/airplay-buffered/mac-to-tv-buffered-session.pcap.
/// The Mac refuses to stream to a receiver without this clock, so drifting any
/// of these fields silently breaks real-sender interop.
/// </summary>
public class PtpGrandmasterWireTests
{
    private static readonly byte[] ClockId = [0x8E, 0x7E, 0xAA, 0xFF, 0xFE, 0x43, 0xD2, 0xB5];

    [Fact]
    public void GmSync_MatchesTvShape()
    {
        var p = PtpWire.BuildGmSync(7, ClockId);
        Assert.Equal(44, p.Length);
        Assert.Equal(0x10, p[0]);                                        // gPTP majorSdoId | Sync
        Assert.Equal(0x02, p[1]);                                        // PTPv2
        Assert.Equal(44, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2)));
        Assert.Equal(0x0600, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6)));   // UNICAST | TWO_STEP
        Assert.Equal(ClockId, p[20..28]);
        Assert.Equal(32768, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(28)));   // source port id
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(30)));
        Assert.Equal(unchecked((byte)(sbyte)-3), p[33]);                 // 8 Hz
    }

    [Fact]
    public void GmFollowUp_CarriesTimestampAndGptpTlv()
    {
        var p = PtpWire.BuildGmFollowUp(7, ClockId, seconds: 1234, nanos: 567);
        Assert.Equal(76, p.Length);
        Assert.Equal(0x18, p[0]);                                        // gPTP | Follow_Up
        Assert.Equal(0x0600, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6)));
        var (secs, nanos) = PtpWire.ParseTimestamp(p.AsSpan(34, 10));
        Assert.Equal(1234UL, secs);
        Assert.Equal(567U, nanos);
        Assert.Equal(0x0003, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(44))); // org-extension TLV
        Assert.Equal(unchecked((byte)(sbyte)-3), p[33]);
    }

    [Fact]
    public void GmDelayResp_EchoesRequesterSequenceAndPortIdentity()
    {
        var req = PtpWire.BuildDelayReq(99, [1, 2, 3, 4, 5, 6, 7, 8], 0, 0);
        var p = PtpWire.BuildGmDelayResp(req, ClockId, seconds: 42, nanos: 43);
        Assert.Equal(54, p.Length);
        Assert.Equal(0x19, p[0]);                                        // gPTP | Delay_Resp
        Assert.Equal(99, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(30)));
        Assert.Equal(req[20..30], p[44..54]);                            // requestingPortIdentity
        Assert.Equal((byte)0, p[33]);                                    // log interval 0 (1 s)
    }

    [Fact]
    public void GmAnnounce_WinsBmcaAgainstMacAndCarriesPathTrace()
    {
        var p = PtpWire.BuildGmAnnounce(3, ClockId);
        Assert.Equal(76, p.Length);
        Assert.Equal(0x1B, p[0]);                                        // gPTP | Announce
        Assert.Equal(0x0600, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6)));
        Assert.Equal(248, p[47]);                                        // priority1 beats Mac's 250
        Assert.True(p[47] < 250);
        Assert.Equal(248, p[48]);                                        // clockClass
        Assert.Equal(0xFE, p[49]);                                       // accuracy unknown
        Assert.Equal(17258, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(50)));
        Assert.Equal(248, p[52]);                                        // priority2
        Assert.Equal(ClockId, p[53..61]);                                // grandmaster identity
        Assert.Equal(0xA0, p[63]);                                       // internal oscillator
        Assert.Equal(0x0008, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(64))); // path-trace TLV
        Assert.Equal(8, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(66)));
        Assert.Equal(ClockId, p[68..76]);
    }

    [Fact]
    public void GmSignaling_RequestsEightHzSyncNoAnnounces()
    {
        var p = PtpWire.BuildGmSignaling(5, ClockId);
        Assert.Equal(60, p.Length);
        Assert.Equal(0x1C, p[0]);                                        // gPTP | Signaling
        Assert.Equal((byte)127, p[33]);
        Assert.All(p[34..44], b => Assert.Equal(0, b));                  // zeroed target port identity
        Assert.Equal(0x0003, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(44)));
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(46)));     // TV lengthField
        Assert.Equal(unchecked((byte)(sbyte)-3), p[55]);                 // timeSyncInterval
        Assert.Equal(0x80, p[56]);                                       // announceInterval: stop
        Assert.Equal(0x03, p[57]);
    }
}
