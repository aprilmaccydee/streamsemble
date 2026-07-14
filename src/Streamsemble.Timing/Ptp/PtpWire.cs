using System.Buffers.Binary;

namespace Streamsemble.Timing.Ptp;

/// <summary>gPTP (IEEE 1588 / 802.1AS) message types used for AirPlay 2 timing.</summary>
public enum PtpMessageType : byte
{
    Sync = 0x00,
    DelayReq = 0x01,
    FollowUp = 0x08,
    DelayResp = 0x09,
    Announce = 0x0B,
    Signaling = 0x0C,
}

/// <summary>
/// Wire encoding for the gPTP messages an AirPlay 2 sender exchanges with the
/// receiver on UDP 319/320. Byte layouts match airplay2-rs / OwnTone exactly.
/// </summary>
public static class PtpWire
{
    public const int EventPort = 319;
    public const int GeneralPort = 320;

    private static readonly byte[] OrgGptp = [0x00, 0x80, 0xC2];
    private static readonly byte[] OrgApple = [0x00, 0x0D, 0x93];

    /// <summary>34-byte common PTP header.</summary>
    public static void WriteHeader(Span<byte> buf, PtpMessageType type, ushort sequenceId, ReadOnlySpan<byte> clockIdentity, ushort messageLength, ushort flags = 0)
    {
        buf[..34].Clear();
        buf[0] = (byte)type;
        buf[1] = 0x02; // version 2
        BinaryPrimitives.WriteUInt16BigEndian(buf[2..], messageLength);
        // domain 0, reserved, flags
        BinaryPrimitives.WriteUInt16BigEndian(buf[6..], flags);
        // correction 0, reserved
        clockIdentity[..8].CopyTo(buf[20..]);   // source port identity: clock id
        BinaryPrimitives.WriteUInt16BigEndian(buf[28..], 1); // port number 1
        BinaryPrimitives.WriteUInt16BigEndian(buf[30..], sequenceId);
        buf[32] = type switch
        {
            PtpMessageType.Sync => 0,
            PtpMessageType.DelayReq => 1,
            PtpMessageType.FollowUp => 2,
            PtpMessageType.DelayResp => 3,
            _ => 5,
        };
        buf[33] = 0; // log message interval
    }

    public static PtpMessageType? ParseType(ReadOnlySpan<byte> data)
    {
        if (data.Length < 34)
        {
            return null;
        }

        var raw = (byte)(data[0] & 0x0F);
        return Enum.IsDefined(typeof(PtpMessageType), raw) ? (PtpMessageType)raw : null;
    }

    public static ushort ParseSequence(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16BigEndian(data[30..]);

    /// <summary>10-byte timestamp: 48-bit seconds BE + 32-bit nanoseconds BE.</summary>
    public static void WriteTimestamp(Span<byte> buf, ulong seconds, uint nanoseconds)
    {
        Span<byte> sec = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(sec, seconds);
        sec[2..8].CopyTo(buf);
        BinaryPrimitives.WriteUInt32BigEndian(buf[6..], nanoseconds);
    }

    public static (ulong Seconds, uint Nanos) ParseTimestamp(ReadOnlySpan<byte> data)
    {
        Span<byte> sec = stackalloc byte[8];
        data[..6].CopyTo(sec[2..]);
        return (BinaryPrimitives.ReadUInt64BigEndian(sec), BinaryPrimitives.ReadUInt32BigEndian(data[6..]));
    }

    public static ulong TimestampToNanos(ulong seconds, uint nanos) => seconds * 1_000_000_000UL + nanos;

    // --- Message builders ---------------------------------------------------

    /// <summary>Sync (event port): two-step, timestamp carried in the Follow_Up.</summary>
    public static byte[] BuildSync(ushort seq, ReadOnlySpan<byte> clockId)
    {
        var packet = new byte[44];
        WriteHeader(packet, PtpMessageType.Sync, seq, clockId, 44, flags: 0x0200);
        return packet;
    }

    /// <summary>Follow_Up (general port): precise origin timestamp + gPTP Follow-Up Info TLV.</summary>
    public static byte[] BuildFollowUp(ushort seq, ReadOnlySpan<byte> clockId, ulong seconds, uint nanos)
    {
        var tlv = BuildFollowUpInfoTlv();
        var packet = new byte[44 + tlv.Length];
        WriteHeader(packet, PtpMessageType.FollowUp, seq, clockId, (ushort)(44 + tlv.Length));
        WriteTimestamp(packet.AsSpan(34), seconds, nanos);
        tlv.CopyTo(packet.AsSpan(44));
        return packet;
    }

    /// <summary>Delay_Req (event port): our T3 timestamp.</summary>
    public static byte[] BuildDelayReq(ushort seq, ReadOnlySpan<byte> clockId, ulong seconds, uint nanos)
    {
        var packet = new byte[44];
        WriteHeader(packet, PtpMessageType.DelayReq, seq, clockId, 44);
        WriteTimestamp(packet.AsSpan(34), seconds, nanos);
        return packet;
    }

    /// <summary>Delay_Resp (general port): echoes the requester's sequence + port identity with our receive timestamp.</summary>
    public static byte[] BuildDelayResp(ReadOnlySpan<byte> delayReq, ReadOnlySpan<byte> clockId, ulong seconds, uint nanos)
    {
        var packet = new byte[54];
        WriteHeader(packet, PtpMessageType.DelayResp, ParseSequence(delayReq), clockId, 54);
        WriteTimestamp(packet.AsSpan(34), seconds, nanos);
        delayReq[20..30].CopyTo(packet.AsSpan(44)); // requestingPortIdentity = requester's source port identity
        return packet;
    }

    /// <summary>Announce (general port): grandmaster candidacy. Lower priority1 wins BMCA.</summary>
    public static byte[] BuildAnnounce(ushort seq, ReadOnlySpan<byte> clockId, byte clockClass, byte priority1)
    {
        var packet = new byte[64];
        WriteHeader(packet, PtpMessageType.Announce, seq, clockId, 64);
        packet[33] = 1; // announce interval 2^1
        // 34..44 origin timestamp = 0
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(44), 37); // currentUtcOffset
        packet[47] = priority1;
        packet[48] = clockClass;
        packet[49] = 0xFE;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(50), 0xFFFF);
        packet[52] = 128; // priority2
        clockId[..8].CopyTo(packet.AsSpan(53)); // grandmaster identity
        packet[63] = 0xA0; // timeSource: internal oscillator
        return packet;
    }

    /// <summary>Mac-style Signaling with the 802.1AS interval-request TLV and Apple TLVs.</summary>
    public static byte[] BuildMacSignaling(ushort seq, ReadOnlySpan<byte> clockId)
    {
        var mir = BuildMessageIntervalTlv(-3, -2, 0x02);
        var apple01 = BuildAppleTlv([0x00, 0x00, 0x01],
            [0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x27, 0x10, 0x00, 0x00, 0x27, 0x10, 0x00, 0x00, 0x00, 0x00]);
        var apple05 = BuildAppleTlv([0x00, 0x00, 0x05],
            [0x00, 0x0f, 0x00, 0x00, 0x00, 0x00, 0x27, 0x10, 0x00, 0x00, 0x27, 0x10, 0x00, 0x00, 0x00, 0x00,
             0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        return BuildSignaling(seq, clockId, mir, apple01, apple05);
    }

    /// <summary>Stop Signaling: interval-request TLV with all intervals = 0x7E (stop), sent when yielding master.</summary>
    public static byte[] BuildStopSignaling(ushort seq, ReadOnlySpan<byte> clockId)
    {
        var stop = BuildMessageIntervalTlv(0x7E, 0x7E, 0x00, announceInterval: 0x7E);
        return BuildSignaling(seq, clockId, stop);
    }

    private static byte[] BuildSignaling(ushort seq, ReadOnlySpan<byte> clockId, params byte[][] tlvs)
    {
        var body = 10 + tlvs.Sum(t => t.Length); // 10-byte target port identity (wildcard) + TLVs
        var packet = new byte[34 + body];
        WriteHeader(packet, PtpMessageType.Signaling, seq, clockId, (ushort)(34 + body));
        packet.AsSpan(34, 10).Fill(0xFF); // target port identity: wildcard
        var offset = 44;
        foreach (var tlv in tlvs)
        {
            tlv.CopyTo(packet.AsSpan(offset));
            offset += tlv.Length;
        }

        return packet;
    }

    private static byte[] BuildFollowUpInfoTlv()
    {
        var buf = new byte[4 + 28];
        BinaryPrimitives.WriteUInt16BigEndian(buf, 0x0003); // OrganizationExtension
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), 28);
        OrgGptp.CopyTo(buf, 4);
        buf[7] = 0x00; buf[8] = 0x00; buf[9] = 0x01; // subtype Follow-Up Info
        // remaining 22 bytes zero (rate offset, gm time base, phase change, freq change)
        return buf;
    }

    private static byte[] BuildMessageIntervalTlv(sbyte linkDelay, sbyte timeSync, byte flags, sbyte announceInterval = -2)
    {
        var buf = new byte[4 + 10];
        BinaryPrimitives.WriteUInt16BigEndian(buf, 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), 10);
        OrgGptp.CopyTo(buf, 4);
        buf[7] = 0x00; buf[8] = 0x00; buf[9] = 0x02; // subtype Message Interval Request
        buf[10] = (byte)linkDelay;
        buf[11] = (byte)timeSync;
        buf[12] = (byte)announceInterval;
        buf[13] = flags;
        return buf;
    }

    private static byte[] BuildAppleTlv(byte[] subtype, byte[] payload)
    {
        var buf = new byte[4 + 6 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(buf, 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)(6 + payload.Length));
        OrgApple.CopyTo(buf, 4);
        subtype.CopyTo(buf, 7);
        payload.CopyTo(buf, 10);
        return buf;
    }
}
