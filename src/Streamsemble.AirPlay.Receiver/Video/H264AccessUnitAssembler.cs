using Microsoft.Extensions.Logging;
using Streamsemble.Core.Video;

namespace Streamsemble.AirPlay.Receiver.Video;

/// <summary>
/// Turns the payloads of a mirroring data channel into stamped
/// <see cref="VideoFrame"/>s. The protocol delivers one access unit per video
/// packet already, so the work is not reassembly so much as bookkeeping:
/// validate the AVCC framing (the first thing that breaks when a stream
/// decrypts wrong), track the parameter sets the config packets carry, and put
/// them back in front of every keyframe.
///
/// That last part is the one that matters downstream. A Mac sends SPS/PPS once,
/// in a config packet, and never again — so a decoder that joins later, or an
/// output session that starts on a keyframe, has nothing to initialize with.
/// Inlining the parameter sets ahead of each IDR makes every keyframe a
/// self-contained entry point, which is what lets the TV be handed a running
/// stream mid-flight.
/// </summary>
public sealed class H264AccessUnitAssembler(ILogger logger)
{
    private VideoCodecConfig? _config;
    private long _accessUnits;
    private long _keyframes;
    private long _rejected;

    /// <summary>Parameter sets currently in force, or null before the first config packet.</summary>
    public VideoCodecConfig? Config => _config;

    public long AccessUnitCount => _accessUnits;

    public long KeyframeCount => _keyframes;

    /// <summary>Access units dropped because their framing did not parse.</summary>
    public long RejectedCount => _rejected;

    /// <summary>
    /// Accepts a codec-config payload (an avcC record). Returns the parsed
    /// config when it is new or changed, null when it repeats what we have —
    /// senders re-send it on every resolution change and some on a timer.
    /// </summary>
    public VideoCodecConfig? AcceptConfig(ReadOnlySpan<byte> avcC)
    {
        VideoCodecConfig parsed;
        try
        {
            parsed = VideoCodecConfig.Parse(avcC);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "mirror codec config rejected ({Bytes} B)", avcC.Length);
            return null;
        }

        if (_config is { } current
            && current.Sps.AsSpan().SequenceEqual(parsed.Sps)
            && current.Pps.AsSpan().SequenceEqual(parsed.Pps))
        {
            return null;
        }

        _config = parsed;
        logger.LogInformation("mirror video configured: {Config}", parsed.Describe());
        return parsed;
    }

    /// <summary>
    /// Accepts a decrypted video payload and stamps it. Returns null when the
    /// framing does not parse — the caller keeps reading rather than tearing
    /// the session down, because a single bad unit is a glitch while a stream
    /// that never parses shows up as a climbing <see cref="RejectedCount"/>.
    /// </summary>
    public VideoFrame? AcceptAccessUnit(ReadOnlySpan<byte> accessUnit, long targetNanos)
    {
        List<H264NalUnit> units;
        try
        {
            units = H264Nal.Split(accessUnit);
        }
        catch (InvalidDataException ex)
        {
            if (_rejected++ == 0)
            {
                logger.LogWarning(ex,
                    "mirror video payload is not AVCC framed — if this repeats, the stream key is wrong");
            }

            return null;
        }

        var isKeyframe = units.Any(u => u.IsIdrSlice);
        var carriesParameterSets = units.Any(u => u.IsParameterSet);
        var data = accessUnit.ToArray();

        if (isKeyframe && !carriesParameterSets && _config is { } config)
        {
            var prefix = config.ToAvccParameterSets();
            var combined = new byte[prefix.Length + data.Length];
            prefix.CopyTo(combined, 0);
            data.CopyTo(combined, prefix.Length);
            data = combined;
        }

        _accessUnits++;
        if (isKeyframe)
        {
            _keyframes++;
        }

        return new VideoFrame(data, targetNanos, isKeyframe);
    }
}
