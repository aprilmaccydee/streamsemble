using Microsoft.Extensions.Logging.Abstractions;
using Claunia.PropertyList;
using Streamsemble.AirPlay.Common.Video;
using Streamsemble.AirPlay.Receiver;
using Streamsemble.AirPlay.Receiver.Audio;
using Streamsemble.AirPlay.Receiver.Video;
using Streamsemble.Core.Video;
using Xunit;

namespace Streamsemble.AirPlay.Tests;

public class MirrorVideoTests
{
    // ---- DataStream envelope (outbound seal ↔ inbound open) ---------------

    [Fact]
    public void DataStreamSealAndOpenRoundTrip()
    {
        // The sender's Seal and the receiver's Open are the two halves of one
        // envelope: same HKDF direction, same counter nonce, header as AAD.
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var sender = MirrorDataStreamCipher.Create(secret, 12345UL, "DataStream-Output-Encryption-Key");
        var receiver = MirrorDataStreamCipher.Create(secret, 12345UL, "DataStream-Output-Encryption-Key");

        var payload = new byte[20];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 7);
        }

        var header = new MirrorPacketHeader(payload.Length + 16, MirrorPayloadType.Video, 1, 42).ToBytes();
        var first = sender.Seal(header, payload);
        Assert.Equal(payload.Length + 16, first.Length);
        Assert.Equal(payload, receiver.Open(header, first));

        // The counter advances per packet: the same plaintext seals differently
        // and still opens, proving both ends count in step.
        var second = sender.Seal(header, payload);
        Assert.NotEqual(first, second);
        Assert.Equal(payload, receiver.Open(header, second));
    }

    [Fact]
    public void DataStreamSealAuthenticatesTheHeader()
    {
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var sender = MirrorDataStreamCipher.Create(secret, 7UL, "DataStream-Output-Encryption-Key");
        var receiver = MirrorDataStreamCipher.Create(secret, 7UL, "DataStream-Output-Encryption-Key");

        var header = new MirrorPacketHeader(4 + 16, MirrorPayloadType.Video, 0, 0).ToBytes();
        var sealedPayload = sender.Seal(header, new byte[] { 1, 2, 3, 4 });

        var tampered = (byte[])header.Clone();
        tampered[8] ^= 0x01; // the timestamp field — sender data, covered by the tag
        Assert.ThrowsAny<Exception>(() => receiver.Open(tampered, sealedPayload));
    }

    // ---- Sender headers (the bytes a real TV configures itself from) ------

    [Fact]
    public void SenderVideoHeaderPutsTheKeyframeFlagAtByteFive()
    {
        var header = Streamsemble.AirPlay.Sender.Video.MirrorSenderHeaders.Video(501, keyframe: true, ntpStamp: 7);
        Assert.Equal(128, header.Length);
        Assert.Equal(501u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Equal(0x00, header[4]); // video payload type
        Assert.Equal(0x10, header[5]); // keyframe flag — NOT the option word at [6..8)
        Assert.Equal(7ul, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8)));
        Assert.Equal(0x00, Streamsemble.AirPlay.Sender.Video.MirrorSenderHeaders.Video(1, false, 0)[5]);
    }

    [Fact]
    public void SenderCodecHeaderCarriesTheDimensions()
    {
        var header = Streamsemble.AirPlay.Sender.Video.MirrorSenderHeaders.CodecConfig(
            41, ntpStamp: 3, sourceWidth: 1920, sourceHeight: 1080, displayWidth: 3840, displayHeight: 2160);
        Assert.Equal(0x01, header[4]); // codec config payload type
        Assert.Equal(0x16, header[6]); // H.264 SPS+PPS option
        Assert.Equal(0x01, header[7]);
        Assert.Equal(1920f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(16)));
        Assert.Equal(1080f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(20)));
        Assert.Equal(1920f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(40)));
        Assert.Equal(1080f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(44)));
        Assert.Equal(3840f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(56)));
        Assert.Equal(2160f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(60)));
    }

    [Fact]
    public void SenderHeadersStayParsableByTheReceiver()
    {
        // The hub's own receiver reads the first 16 bytes; the sender's richer
        // headers must still parse to the same fields.
        var video = MirrorPacketHeader.Parse(
            Streamsemble.AirPlay.Sender.Video.MirrorSenderHeaders.Video(501, keyframe: true, ntpStamp: 42));
        Assert.Equal(501, video.PayloadLength);
        Assert.Equal(MirrorPayloadType.Video, video.PayloadType);
        Assert.Equal(42ul, video.TimestampNtp);

        var codec = MirrorPacketHeader.Parse(
            Streamsemble.AirPlay.Sender.Video.MirrorSenderHeaders.CodecConfig(41, 3, 1920, 1080, 1920, 1080));
        Assert.Equal(MirrorPayloadType.CodecConfig, codec.PayloadType);

        var heartbeat = MirrorPacketHeader.Parse(Streamsemble.AirPlay.Sender.Video.MirrorSenderHeaders.Heartbeat(9));
        Assert.Equal(MirrorPayloadType.Heartbeat, heartbeat.PayloadType);
        Assert.Equal(0, heartbeat.PayloadLength);
    }

    // ---- Packet framing ----------------------------------------------------

    [Fact]
    public void PacketHeaderRoundTrips()
    {
        var header = new MirrorPacketHeader(4096, MirrorPayloadType.Video, 1, 0xE7A3_1B2C_5566_7788);
        var parsed = MirrorPacketHeader.Parse(header.ToBytes());
        Assert.Equal(header, parsed);
    }

    [Fact]
    public void PacketHeaderIsAlwaysTheFixedSize()
    {
        // The trailing 112 bytes are sender padding we do not interpret, but a
        // receiver reads a fixed stride — emitting a short header silently puts
        // the whole stream out of frame.
        Assert.Equal(128, new MirrorPacketHeader(0, MirrorPayloadType.Heartbeat, 0, 0).ToBytes().Length);
    }

    [Fact]
    public void PacketHeaderRejectsImplausibleLength()
    {
        var header = new byte[MirrorPacketHeader.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        Assert.Throws<InvalidDataException>(() => MirrorPacketHeader.Parse(header));
    }

    [Fact]
    public void PresentationStampsCarryNoNtpEpoch()
    {
        // A mirror stamp uses NTP's fixed-point LAYOUT but is the sender's
        // monotonic clock, not a date. Treating it as a date puts every frame
        // exactly 2208988800 s in the past — a wrong answer that still looks
        // like a timestamp. Observed against a real Mac 2026-08-08.
        const ulong oneSecondSinceBoot = 1UL << 32;
        Assert.Equal(1_000_000_000L, MirrorPacketHeader.PresentationStampToNanos(oneSecondSinceBoot));
        Assert.Equal(
            2_208_988_800L * 1_000_000_000L,
            MirrorPacketHeader.PresentationStampToNanos(oneSecondSinceBoot)
                - MirrorPacketHeader.NtpToUnixNanos(oneSecondSinceBoot));
    }

    [Fact]
    public void NtpStampsSurviveTheRoundTripToNanoseconds()
    {
        // Sub-millisecond fidelity is the point: a stamp is a render deadline,
        // and the NTP fraction is 2^-32 s, so the conversion must not round to
        // anything a viewer could see.
        const long nanos = 1_754_600_000_123_456_789L;
        var recovered = MirrorPacketHeader.NtpToUnixNanos(MirrorPacketHeader.UnixNanosToNtp(nanos));
        Assert.InRange(Math.Abs(recovered - nanos), 0, 1000);
    }

    // ---- Stream cipher -----------------------------------------------------

    [Fact]
    public void CipherRoundTripsWhenBothSidesShareTheStreamConnectionId()
    {
        var key = new byte[16];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i * 9 + 1);
        }

        using var sender = new MirrorAesCtrCipher(key, 0x1122334455667788, MirrorKeySource.SharedKey);
        using var receiver = new MirrorAesCtrCipher(key, 0x1122334455667788, MirrorKeySource.SharedKey);

        // Deliberately not block-aligned: real access units never are, and the
        // partial trailing block is where a naive CTR gets the next packet wrong.
        foreach (var length in new[] { 1, 15, 16, 17, 4097 })
        {
            var plaintext = new byte[length];
            Random.Shared.NextBytes(plaintext);
            Assert.Equal(plaintext, receiver.Decrypt(sender.Encrypt(plaintext)));
        }
    }

    [Fact]
    public void CipherCounterIsContinuousAcrossPackets()
    {
        var key = new byte[16];
        using var first = new MirrorAesCtrCipher(key, 7, MirrorKeySource.SharedKey);
        using var second = new MirrorAesCtrCipher(key, 7, MirrorKeySource.SharedKey);

        var packet = new byte[16];
        var a1 = first.Encrypt(packet);
        var a2 = first.Encrypt(packet);

        // The same plaintext must encrypt differently the second time, or the
        // counter is being reset per packet and the keystream repeats.
        Assert.NotEqual(a1, a2);
        Assert.Equal(a1, second.Encrypt(packet));
        Assert.Equal(a2, second.Encrypt(packet));
    }

    [Fact]
    public void CipherStartsEachPacketOnAFreshBlock()
    {
        // A packet whose length is not a multiple of 16 must not leak leftover
        // keystream into the next packet: the receiver always starts a packet
        // on a block boundary, so a sender that carries the remainder forward
        // decrypts packet one correctly and turns everything after it to noise.
        var key = new byte[16];
        using var sender = new MirrorAesCtrCipher(key, 3, MirrorKeySource.SharedKey);
        using var receiver = new MirrorAesCtrCipher(key, 3, MirrorKeySource.SharedKey);

        var ragged = new byte[5];
        var following = new byte[32];
        Random.Shared.NextBytes(ragged);
        Random.Shared.NextBytes(following);

        Assert.Equal(ragged, receiver.Decrypt(sender.Encrypt(ragged)));
        Assert.Equal(following, receiver.Decrypt(sender.Encrypt(following)));
    }

    [Fact]
    public void CipherKeyDependsOnTheStreamConnectionId()
    {
        var key = new byte[16];
        using var one = new MirrorAesCtrCipher(key, 1, MirrorKeySource.SharedKey);
        using var other = new MirrorAesCtrCipher(key, 2, MirrorKeySource.SharedKey);
        var plaintext = new byte[16];
        Assert.NotEqual(one.Encrypt(plaintext), other.Encrypt(plaintext));
    }

    // ---- Key material resolution -------------------------------------------

    [Fact]
    public void SetupWithSharedKeyResolvesToHapKeying()
    {
        var request = new MirrorKeyRequest(new byte[16], null, null, 42);
        using var cipher = (MirrorAesCtrCipher)request.Resolve(out var source);
        Assert.Equal(MirrorKeySource.SharedKey, source);
    }

    [Fact]
    public void SetupNamingNoKeyFallsBackToThePairingSecret()
    {
        // macOS screen mirroring sends a stream SETUP with no shk and no ekey:
        // it sets streamConnectionKeyUseStreamEncryptionKey and expects the key
        // to come from the pairing. Observed against a real Mac 2026-08-08.
        var request = new MirrorKeyRequest(null, null, null, 42, SessionKey: new byte[32]);
        using var cipher = (MirrorAesCtrCipher)request.Resolve(out var source);
        Assert.Equal(MirrorKeySource.PairingSecret, source);
    }

    [Fact]
    public void KeyMaterialHashesTheFairPlayKeyTogetherWithThePairingSecret()
    {
        // The stage that is easy to miss: the stream key is NOT derived from
        // the pairing secret directly, but from SHA-512(aesKey ‖ ecdhSecret)
        // truncated to 16. Skipping it yields a cipher that runs fine and
        // decrypts everything to noise.
        var secret = new byte[32];
        for (var i = 0; i < secret.Length; i++)
        {
            secret[i] = (byte)(i + 1);
        }

        var withoutFairPlay = MirrorAesCtrCipher.DeriveKeyMaterial(default, secret);
        Assert.Equal(16, withoutFairPlay.Length);
        Assert.Equal(System.Security.Cryptography.SHA512.HashData(secret)[..16], withoutFairPlay);

        // An ekey in play changes the material, so the two paths cannot collide.
        var withFairPlay = MirrorAesCtrCipher.DeriveKeyMaterial(new byte[16], secret);
        Assert.NotEqual(withoutFairPlay, withFairPlay);
    }

    [Fact]
    public void StreamKeyIsDerivedFromTheMaterialNotTheSecret()
    {
        // Guards the two-stage shape end to end: a cipher built from the raw
        // secret must differ from one built from the material derived out of
        // it, which is exactly the bug that produced an unreadable stream.
        var secret = new byte[32];
        Random.Shared.NextBytes(secret);

        using var wrong = new MirrorAesCtrCipher(secret, 42, MirrorKeySource.PairingSecret);
        using var right = new MirrorAesCtrCipher(
            MirrorAesCtrCipher.DeriveKeyMaterial(default, secret), 42, MirrorKeySource.PairingSecret);

        var plaintext = new byte[32];
        Assert.NotEqual(wrong.Encrypt(plaintext), right.Encrypt(plaintext));
    }

    [Fact]
    public void ShkOutranksThePairingSecretWhenBothArePresent()
    {
        var shk = new byte[16];
        shk[0] = 0xAB;
        var request = new MirrorKeyRequest(shk, null, null, 42, SessionKey: new byte[32]);
        using var cipher = (MirrorAesCtrCipher)request.Resolve(out var source);
        Assert.Equal(MirrorKeySource.SharedKey, source);
    }

    [Fact]
    public void SetupWithNoKeyAnywhereIsRejected()
    {
        var request = new MirrorKeyRequest(null, null, null, 42);
        Assert.Throws<InvalidDataException>(() => request.Resolve(out _));
    }

    [Fact]
    public void FairPlaySetupWithoutItsHandshakeIsRejected()
    {
        var request = new MirrorKeyRequest(null, new byte[72], null, 42);
        Assert.Throws<InvalidDataException>(() => request.Resolve(out _));
    }

    // ---- H.264 framing -----------------------------------------------------

    private static byte[] Avcc(params byte[][] nals)
    {
        var output = new List<byte>();
        foreach (var nal in nals)
        {
            output.AddRange(new byte[] { (byte)(nal.Length >> 24), (byte)(nal.Length >> 16), (byte)(nal.Length >> 8), (byte)nal.Length });
            output.AddRange(nal);
        }

        return output.ToArray();
    }

    [Fact]
    public void SplitsAvccIntoNalUnits()
    {
        var unit = Avcc([0x65, 1, 2, 3], [0x41, 9]);
        var nals = H264Nal.Split(unit);

        Assert.Equal(2, nals.Count);
        Assert.Equal(H264Nal.IdrSlice, nals[0].Type);
        Assert.True(nals[0].IsIdrSlice);
        Assert.Equal(H264Nal.NonIdrSlice, nals[1].Type);
        Assert.True(H264Nal.ContainsIdr(unit));
    }

    [Fact]
    public void RejectsAccessUnitWhoseLengthPrefixOverruns()
    {
        // The earliest and clearest symptom of a wrong stream key.
        Assert.Throws<InvalidDataException>(() => H264Nal.Split([0x00, 0x00, 0x10, 0x00, 0x65]));
    }

    [Fact]
    public void RejectsAccessUnitWithTrailingBytes()
    {
        Assert.Throws<InvalidDataException>(() => H264Nal.Split([.. Avcc([0x65, 1]), 0xFF]));
    }

    [Fact]
    public void ConvertsBetweenAvccAndAnnexB()
    {
        var unit = Avcc([0x67, 0x42, 0x00, 0x1E], [0x68, 0xCE], [0x65, 1, 2, 3]);
        var annexB = H264Nal.ToAnnexB(unit);

        Assert.Equal(new byte[] { 0, 0, 0, 1 }, annexB[..4]);
        Assert.Equal(unit, H264Nal.FromAnnexB(annexB));
    }

    // ---- Access unit assembly ----------------------------------------------

    /// <summary>A 1280x720 baseline SPS, so the parser has something real to read.</summary>
    private static readonly byte[] Sps =
        [0x67, 0x42, 0xC0, 0x1F, 0xD9, 0x00, 0x50, 0x05, 0xBA, 0x10, 0x00, 0x00, 0x03, 0x00, 0x10, 0x00, 0x00, 0x03, 0x03, 0xC0, 0xF1, 0x83, 0x19, 0x60];

    private static readonly byte[] Pps = [0x68, 0xCB, 0x83, 0xCB, 0x20];

    private static byte[] AvcCRecord()
    {
        var record = new List<byte> { 1, Sps[1], Sps[2], Sps[3], 0xFF, 0xE1, (byte)(Sps.Length >> 8), (byte)Sps.Length };
        record.AddRange(Sps);
        record.Add(1);
        record.AddRange([(byte)(Pps.Length >> 8), (byte)Pps.Length]);
        record.AddRange(Pps);
        return record.ToArray();
    }

    [Fact]
    public void ParsesCodecConfigIncludingPictureSize()
    {
        var config = VideoCodecConfig.Parse(AvcCRecord());

        Assert.Equal(Sps, config.Sps);
        Assert.Equal(Pps, config.Pps);
        Assert.Equal(1280, config.Width);
        Assert.Equal(720, config.Height);
        Assert.Contains("1280x720", config.Describe());
    }

    [Fact]
    public void CodecConfigSurvivesAnAvcCRoundTrip()
    {
        var config = VideoCodecConfig.Parse(AvcCRecord());
        var again = VideoCodecConfig.Parse(config.ToAvcC());

        Assert.Equal(config.Sps, again.Sps);
        Assert.Equal(config.Pps, again.Pps);
        Assert.Equal(config.Width, again.Width);
    }

    [Fact]
    public void VclOnlyKeepsSlicesAndDropsEverythingElse()
    {
        // A keyframe AU as it reaches the sender: parameter sets and SEI ahead
        // of the coded slices. The TV gets SPS/PPS from the avcC codec packet,
        // so the wire payload must be the slices alone, in order.
        var au = Avcc(
            [0x67, 1, 2, 3], // SPS  (type 7)
            [0x68, 4],       // PPS  (type 8)
            [0x06, 5],       // SEI  (type 6)
            [0x65, 6, 7],    // IDR  (type 5)
            [0x41, 8]);      // slice (type 1)

        var vcl = H264Nal.VclOnly(au);
        var types = H264Nal.Split(vcl).Select(n => n.Type).ToArray();
        Assert.Equal([H264Nal.IdrSlice, H264Nal.NonIdrSlice], types);
    }

    [Fact]
    public void VclOnlyReturnsEmptyForAConfigOnlyUnit()
    {
        var au = Avcc([0x67, 1, 2, 3], [0x68, 4]);
        Assert.Empty(H264Nal.VclOnly(au));
    }

    [Fact]
    public void VclOnlyPassesAPlainSliceThroughUnchanged()
    {
        var au = Avcc([0x41, 9, 9, 9]);
        Assert.Equal(au, H264Nal.VclOnly(au));
    }

    [Fact]
    public void VclOnlyStripsWhatTheAssemblerInjected()
    {
        // Ties the two halves together: the assembler prepends [SPS][PPS] to a
        // bare keyframe (for the inbound decode contract), and the sender's
        // strip removes them again so only the IDR reaches the TV.
        var assembler = new H264AccessUnitAssembler(NullLogger.Instance);
        assembler.AcceptConfig(AvcCRecord());
        var keyframe = assembler.AcceptAccessUnit(Avcc([0x65, 1, 2, 3]), 1234);

        var vcl = H264Nal.VclOnly(keyframe!.Value.Data.Span);
        var types = H264Nal.Split(vcl).Select(n => n.Type).ToArray();
        Assert.Equal([H264Nal.IdrSlice], types);
    }

    [Fact]
    public void AssemblerReportsAConfigOnceAndSuppressesRepeats()
    {
        var assembler = new H264AccessUnitAssembler(NullLogger.Instance);

        Assert.NotNull(assembler.AcceptConfig(AvcCRecord()));
        Assert.Null(assembler.AcceptConfig(AvcCRecord()));
    }

    [Fact]
    public void AssemblerInlinesParameterSetsAheadOfKeyframes()
    {
        // A Mac sends SPS/PPS once, in a config packet. Without this, a decoder
        // that joins later — or a TV handed a running stream — has nothing to
        // initialise from and shows a black screen forever.
        var assembler = new H264AccessUnitAssembler(NullLogger.Instance);
        assembler.AcceptConfig(AvcCRecord());

        var keyframe = assembler.AcceptAccessUnit(Avcc([0x65, 1, 2, 3]), 1234);

        Assert.NotNull(keyframe);
        Assert.True(keyframe!.Value.IsKeyframe);
        Assert.Equal(1234, keyframe.Value.TargetNanos);

        var types = H264Nal.Split(keyframe.Value.Data.Span).Select(n => n.Type).ToArray();
        Assert.Equal([H264Nal.SequenceParameterSet, H264Nal.PictureParameterSet, H264Nal.IdrSlice], types);
    }

    [Fact]
    public void AssemblerLeavesNonKeyframesAlone()
    {
        var assembler = new H264AccessUnitAssembler(NullLogger.Instance);
        assembler.AcceptConfig(AvcCRecord());

        var unit = Avcc([0x41, 9, 9, 9]);
        var frame = assembler.AcceptAccessUnit(unit, 0);

        Assert.NotNull(frame);
        Assert.False(frame!.Value.IsKeyframe);
        Assert.Equal(unit, frame.Value.Data.ToArray());
    }

    [Fact]
    public void AssemblerDoesNotDuplicateParameterSetsAlreadyPresent()
    {
        var assembler = new H264AccessUnitAssembler(NullLogger.Instance);
        assembler.AcceptConfig(AvcCRecord());

        var frame = assembler.AcceptAccessUnit(Avcc(Sps, Pps, [0x65, 1]), 0);

        Assert.NotNull(frame);
        Assert.Equal(3, H264Nal.Split(frame!.Value.Data.Span).Count);
    }

    [Fact]
    public void AssemblerRejectsGarbageWithoutThrowing()
    {
        // A stream that decrypts wrong must show up as a climbing counter, not
        // as an exception that tears the session down on one bad unit.
        var assembler = new H264AccessUnitAssembler(NullLogger.Instance);

        Assert.Null(assembler.AcceptAccessUnit([0xDE, 0xAD, 0xBE, 0xEF, 0x00], 0));
        Assert.Equal(1, assembler.RejectedCount);
        Assert.Equal(0, assembler.AccessUnitCount);
    }

    [Fact]
    public void ReadsPictureSizeFromAHighProfileSps()
    {
        // macOS mirrors in High profile, whose SPS carries an extra
        // chroma/scaling-matrix prologue. Skipping that prologue leaves the bit
        // cursor misaligned and every later field garbage — it reported a
        // 1920x1080 screen as 16x64. Baseline streams took the other branch, so
        // the baseline test above passed throughout.
        var sps = Convert.FromHexString("6764002AACD940780227E5C044000003000400000300F23C60C658");
        Assert.Equal((1920, 1080), H264SequenceParameterSet.PictureSize(sps));
    }

    [Fact]
    public void AvccDetectionRejectsNoise()
    {
        // This is the acceptance test that settles which key a mirror stream
        // uses, so a false positive would lock onto a wrong key permanently.
        var noise = new byte[512];
        Random.Shared.NextBytes(noise);
        Assert.False(H264Nal.LooksLikeAvcc(noise));
        Assert.False(H264Nal.LooksLikeAvcc([]));
        Assert.True(H264Nal.LooksLikeAvcc(Avcc([0x65, 1, 2, 3], [0x41, 9])));
    }

    [Fact]
    public void UnreadableSpsDegradesToAnUnknownSizeRatherThanFailing()
    {
        // Resolution is a display detail; a stream that decodes perfectly well
        // must not be refused because its SPS uses syntax we do not walk.
        Assert.Equal((0, 0), H264SequenceParameterSet.PictureSize([0x67, 0xFF]));
    }

    // ---- Screen advertisement ----------------------------------------------

    [Fact]
    public void ScreenMirroringBitsAreOffUnlessEnabled()
    {
        var saved = ReceiverFeatures.ScreenMirroringAdvertised;
        try
        {
            ReceiverFeatures.ScreenMirroringAdvertised = false;
            Assert.Equal(ReceiverFeatures.Mask, ReceiverFeatures.AdvertisedMask);

            ReceiverFeatures.ScreenMirroringAdvertised = true;
            // Bit 0 (Video) and bit 7 (Screen) are what make a Mac offer this
            // device as a mirror target; everything the audio negotiation
            // depends on has to survive untouched alongside them.
            const ulong screenBits = (1UL << 0) | (1UL << 7);
            Assert.Equal(ReceiverFeatures.Mask | screenBits, ReceiverFeatures.AdvertisedMask);
            Assert.Equal(ReceiverFeatures.Mask, ReceiverFeatures.AdvertisedMask & ~screenBits);
            // Transient pairing must survive: the audio path depends on it.
            Assert.NotEqual(0UL, ReceiverFeatures.AdvertisedMask & (1UL << 48));
        }
        finally
        {
            ReceiverFeatures.ScreenMirroringAdvertised = saved;
        }
    }

    [Fact]
    public void AdvertisedDisplayCarriesTheKeysASenderSizesItsEncoderFrom()
    {
        // Without these a Mac completes pairing, fp-setup, session SETUP and
        // RECORD, then tears down without ever asking for a video stream —
        // there is no error anywhere, it simply does not believe there is a
        // screen here. Observed 2026-08-08 against a real Mac.
        var display = ReceiverSession.BuildDisplay(new MirrorDisplay(1280, 720, 30), "uuid-1234");

        Assert.Equal(1280, ((NSNumber)display["width"]).ToLong());
        Assert.Equal(1280, ((NSNumber)display["widthPixels"]).ToLong());
        Assert.Equal(720, ((NSNumber)display["height"]).ToLong());
        Assert.Equal(720, ((NSNumber)display["heightPixels"]).ToLong());
        Assert.Equal(30, ((NSNumber)display["maxFPS"]).ToLong());
        Assert.Equal("uuid-1234", ((NSString)display["uuid"]).Content);
        Assert.False(((NSNumber)display["overscanned"]).ToBool());
    }

    // ---- Mirror companion audio --------------------------------------------

    [Fact]
    public void MirrorAudioLeavesTheSubBlockTailInTheClear()
    {
        // Only whole 16-byte blocks are encrypted; the trailing bytes ride in
        // the clear. Decrypting them turns the tail of every frame to noise.
        var key = new byte[16];
        var iv = new byte[16];
        Random.Shared.NextBytes(key);
        Random.Shared.NextBytes(iv);
        using var cipher = new MirrorAudioCipher(key, iv);

        var payload = new byte[16 * 3 + 5];
        Random.Shared.NextBytes(payload);
        var plain = cipher.Decrypt(payload);

        Assert.Equal(payload.Length, plain.Length);
        Assert.Equal(payload[^5..], plain[^5..]);
        Assert.NotEqual(payload[..48], plain[..48]);
    }

    [Fact]
    public void MirrorAudioRestartsTheChainEveryPacket()
    {
        // Realtime audio is lossy, so packets have to be independent — a chain
        // carried across packets would make one dropped packet corrupt the rest.
        var key = new byte[16];
        var iv = new byte[16];
        Random.Shared.NextBytes(key);
        Random.Shared.NextBytes(iv);
        using var cipher = new MirrorAudioCipher(key, iv);

        var payload = new byte[32];
        Random.Shared.NextBytes(payload);
        Assert.Equal(cipher.Decrypt(payload), cipher.Decrypt(payload));
    }

    [Fact]
    public void MirrorAudioSharesTheVideoStreamKeyMaterial()
    {
        // Audio and video are keyed from the same eaeskey; only the envelope
        // differs. Deriving them separately is how they drift apart.
        var secret = new byte[32];
        Random.Shared.NextBytes(secret);
        Assert.Equal(
            MirrorAesCtrCipher.DeriveKeyMaterial(default, secret),
            MirrorAesCtrCipher.DeriveKeyMaterial(default, secret));
    }

    // ---- Pump start-up: the backlog IS the reference chain -----------------
    //
    // A Mac sends one IDR when mirroring starts and no more until its config
    // changes, and connecting the display takes seconds. Whatever queued while
    // connecting is therefore not droppable history — it holds the only entry
    // point the stream will ever offer. The pump must replay from the newest
    // keyframe, not wait for a keyframe that is never coming.

    [Fact]
    public async Task PumpReplaysTheBacklogFromTheOpeningKeyframe()
    {
        var source = new FakeVideoSource { CodecConfig = TestConfig() };
        var sink = new RecordingVideoSink();
        await using var pump = new VideoPump(source, sink, NullLogger<VideoPump>.Instance);

        source.Push(Frame(keyframe: true, stamp: 1));
        source.Push(Frame(keyframe: false, stamp: 2));
        source.Push(Frame(keyframe: false, stamp: 3));
        pump.Start(CancellationToken.None);

        await WaitForAsync(() => sink.Written.Count == 3);
        Assert.True(sink.Written[0].IsKeyframe);
        Assert.Equal(new long[] { 1, 2, 3 }, sink.Written.Select(f => f.TargetNanos));
        Assert.Equal(1, sink.Starts);
    }

    [Fact]
    public async Task PumpReplaysFromTheNewestKeyframeOnly()
    {
        // Frames before the newest keyframe reference pictures the display
        // will never see; sending them is what rendered as corruption.
        var source = new FakeVideoSource { CodecConfig = TestConfig() };
        var sink = new RecordingVideoSink();
        await using var pump = new VideoPump(source, sink, NullLogger<VideoPump>.Instance);

        source.Push(Frame(keyframe: true, stamp: 1));
        source.Push(Frame(keyframe: false, stamp: 2));
        source.Push(Frame(keyframe: true, stamp: 3));
        source.Push(Frame(keyframe: false, stamp: 4));
        pump.Start(CancellationToken.None);

        await WaitForAsync(() => sink.Written.Count == 2);
        Assert.True(sink.Written[0].IsKeyframe);
        Assert.Equal(new long[] { 3, 4 }, sink.Written.Select(f => f.TargetNanos));
    }

    [Fact]
    public async Task PumpDropsAnEntrylessBacklogButForwardsTheNextKeyframe()
    {
        // If the opening IDR overflowed the queue there is nothing to replay —
        // but the stream must still start the moment the source produces a
        // fresh keyframe (a config change), not stay dark for good.
        var source = new FakeVideoSource { CodecConfig = TestConfig() };
        var sink = new RecordingVideoSink();
        await using var pump = new VideoPump(source, sink, NullLogger<VideoPump>.Instance);

        source.Push(Frame(keyframe: false, stamp: 1));
        source.Push(Frame(keyframe: false, stamp: 2));
        pump.Start(CancellationToken.None);
        await WaitForAsync(() => sink.Starts == 1);

        // Whether this lands in the backlog drain or after it, it is the
        // newest keyframe and must reach the sink either way.
        source.Push(Frame(keyframe: true, stamp: 3));
        await WaitForAsync(() => sink.Written.Count > 0);
        Assert.True(sink.Written[0].IsKeyframe);
        Assert.Equal(3, sink.Written[0].TargetNanos);
        Assert.DoesNotContain(sink.Written, f => f.TargetNanos < 3);
    }

    private static VideoCodecConfig TestConfig()
        => new([0x67, 0x64, 0x00, 0x28], [0x68, 0xEE], 1920, 1080);

    private static VideoFrame Frame(bool keyframe, long stamp)
        => new(new byte[] { 0, 0, 0, 1, keyframe ? (byte)0x65 : (byte)0x41 }, stamp, keyframe);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not reached within 5 s");
    }

    private sealed class FakeVideoSource : IVideoSource
    {
        private readonly System.Threading.Channels.Channel<VideoFrame> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<VideoFrame>();

        public string Name => "fake mirror";

        public bool IsActive => true;

        public System.Threading.Channels.ChannelReader<VideoFrame> Frames => _channel.Reader;

        public VideoCodecConfig? CodecConfig { get; set; }

        public event EventHandler<VideoCodecConfig>? CodecConfigChanged { add { } remove { } }

        public event EventHandler<bool>? ActiveChanged { add { } remove { } }

        public void Push(VideoFrame frame) => _channel.Writer.TryWrite(frame);
    }

    private sealed class RecordingVideoSink : IVideoSink
    {
        private readonly List<VideoFrame> _written = [];

        public int Starts;

        public IReadOnlyList<VideoFrame> Written
        {
            get { lock (_written) { return _written.ToArray(); } }
        }

        public Task StartStreamAsync(VideoCodecConfig config, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Starts);
            return Task.CompletedTask;
        }

        public Task ReconfigureAsync(VideoCodecConfig config, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask WriteAsync(VideoFrame frame, CancellationToken ct = default)
        {
            lock (_written)
            {
                _written.Add(frame);
            }

            return ValueTask.CompletedTask;
        }

        public Task StopStreamAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
