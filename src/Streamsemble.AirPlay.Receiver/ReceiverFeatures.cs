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

    /// <summary>
    /// What a Mac needs to see before it will mirror its screen here:
    /// <list type="bullet">
    /// <item>bit 0 (Video) and bit 7 (Screen) — make it offer this device as a
    ///   mirror target rather than just a speaker. Proven: with these set it
    ///   sends <c>isScreenMirroringSession</c> and opens a type-110 stream.</item>
    /// </list>
    /// Bit 27 (legacy pairing) was tried here on the theory that it would make
    /// the Mac hand over a FairPlay <c>ekey</c>. It does not: the session SETUP
    /// came back byte-identical, with no key of any kind. The reason is deeper
    /// than a feature bit — see <see cref="ScreenMirroringAdvertised"/>.
    /// </summary>
    private const ulong ScreenMirroringBits = (1UL << 0) | (1UL << 7);

    /// <summary>
    /// Whether to advertise screen mirroring. Off by default, and that is a
    /// deliberate refusal to gamble: the mask above is not a guess, it is the
    /// exact surface a real Mac accepted, and every previous change to it broke
    /// audio in a way that took rounds of captures to diagnose (clearing bit 26
    /// and setting 14; clearing 19/20 and getting silent limbo). Adding bits
    /// moves the receiver into a different device class in the sender's eyes,
    /// and until a Mac→hub capture in debug/airplay-mirror/ shows it still
    /// negotiating audio the same way, the working path stays untouched by
    /// default. Set AirPlayReceiver:ScreenMirroring=true to take the capture.
    ///
    /// KNOWN LIMIT (2026-08-08, established against a real Mac): with these
    /// bits set the Mac DOES offer to mirror and opens a type-110 stream, but
    /// the video cannot be decrypted, and the cause is the pairing rather than
    /// anything downstream. The mirror stream key is
    /// <c>SHA-512(fairplayKey ‖ ecdhSecret)[:16]</c>, and under transient
    /// pairing NEITHER input exists: the Mac sends no <c>ekey</c>, and a
    /// transient session's secret is an SRP session key, not the X25519 ECDH
    /// secret the derivation means. Every receiver that mirrors successfully
    /// (UxPlay, PhairPlay, openairplay) uses a persistent pairing, where
    /// pair-verify produces a real ECDH secret and the Mac sends the ekey.
    /// Mirroring therefore needs receiver-side pair-verify, not another bit.
    /// </summary>
    public static bool ScreenMirroringAdvertised { get; set; }

    /// <summary>The mask actually advertised, screen bits included when enabled.</summary>
    public static ulong AdvertisedMask => ScreenMirroringAdvertised ? Mask | ScreenMirroringBits : Mask;

    /// <summary>Low 32 bits first — the split-hex form every AirPlay TXT parser expects.</summary>
    private static string FeaturesTxt => $"0x{unchecked((uint)AdvertisedMask):X},0x{(uint)(AdvertisedMask >> 32):X}";

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
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, AdvertisedMask);
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
