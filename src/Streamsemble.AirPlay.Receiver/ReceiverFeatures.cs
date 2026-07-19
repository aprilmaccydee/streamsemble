namespace Streamsemble.AirPlay.Receiver;

/// <summary>
/// The AirPlay 2 capability surface we advertise. The mask is the one a real
/// Mac accepted from a transient-pairing buffered receiver (Sonos capture in
/// debug/airplay-buffered): bit 48 = transient HomeKit pairing (makes senders
/// use X-Apple-HKP: 4 and skip fp-setup), bit 40 = buffered audio, plus the
/// audio/metadata bits of that mask. TXT keys mirror the same device's
/// records, minus vendor-specific extras.
/// </summary>
public static class ReceiverFeatures
{
    // Auth bits, learned the hard way — Macs want exactly one they can finish:
    // - Bit 26 (MFi/auth-setup) CLEAR: set in the Sonos mask this was copied
    //   from, it makes Macs POST /auth-setup — a Curve25519+MFi-certificate
    //   exchange only real MFi silicon can sign. Our 501 aborted the session.
    // - Bit 14 (FairPlay auth) SET — REQUIRED: with neither 26 nor 14 the Mac
    //   completes transient pair-setup and then drops the connection without a
    //   single encrypted request, three times, then "Could not connect"
    //   (re-proven against the full working stack, so round 2's diagnosis was
    //   right: senders need one auth mechanism they can finish, and MFi needs
    //   silicon we don't have).
    // - Bits 19/20 (audio formats) are REQUIRED: clearing them made the Mac
    //   route audio to us but never send a stream SETUP at all (silent limbo).
    //   Bit 21 tested neutral either way; left clear to match Sonos.
    //
    // None of the TXT/info surface chooses the stream TYPE: macOS system
    // output computes ALAC realtime (audioFormat 0x40000, engine RTAudio) from
    // the picker for EVERY audio-class receiver — including a real Sonos
    // (AirPlayXPCHelper log, 2026-07-19). Music-app sessions use buffered.
    // The receiver therefore accepts both stream types.
    public const ulong Mask = 0x0801C340405FCA00;

    /// <summary>Low 32 bits first — the split-hex form every AirPlay TXT parser expects.</summary>
    private static string FeaturesTxt => $"0x{unchecked((uint)Mask):X},0x{(uint)(Mask >> 32):X}";

    /// <summary>
    /// fex = the features mask as 8 bytes little-endian PLUS extended
    /// capability bytes, base64 with padding stripped. The extras are the
    /// Kitchen Sonos's exact trailing bytes (extended bits 68/72/77 — live
    /// TXT+/info 2026-07-19): every receiver a Mac demonstrably streams to
    /// advertises extended bytes (TV: bits 70/75), we advertised none.
    /// </summary>
    public static string FeaturesEx
    {
        get
        {
            var bytes = new byte[10];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, Mask);
            bytes[8] = 0x40; // TV's extras (extended bits 70/75) — the modern
            bytes[9] = 0x08; // AirPlay 3.5-sdk receiver class we now mirror.
            return Convert.ToBase64String(bytes).TrimEnd('=');
        }
    }

    public static IReadOnlyDictionary<string, string> TxtRecords(ReceiverIdentity identity) => new Dictionary<string, string>
    {
        ["acl"] = "0",
        ["deviceid"] = identity.DeviceId,
        ["features"] = FeaturesTxt,
        ["fex"] = FeaturesEx,
        ["rsf"] = "0x0",
        ["fv"] = "p20.1.0",
        ["flags"] = "0x4",
        ["model"] = ReceiverConstants.Model,
        ["manufacturer"] = "Streamsemble",
        ["protovers"] = "1.1",
        ["srcvers"] = ReceiverConstants.SourceVersion,
        ["pi"] = identity.Pi,
        ["gid"] = identity.Pi,
        ["gcgl"] = "0",
        ["pk"] = identity.PkHex,
    };

    public static IReadOnlyDictionary<string, string> RaopTxtRecords(ReceiverIdentity identity) => new Dictionary<string, string>
    {
        ["cn"] = "0,1",
        ["da"] = "true",
        ["et"] = "0,1",
        ["ft"] = FeaturesTxt,
        ["md"] = "0,2",
        ["am"] = ReceiverConstants.Model,
        ["sf"] = "0x4",
        ["tp"] = "UDP",
        ["vn"] = "65537",
        ["vs"] = ReceiverConstants.SourceVersion,
        ["ov"] = "15.0",
        ["pk"] = identity.PkHex,
    };
}
