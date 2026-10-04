using Streamsemble.Core.Audio;

namespace Streamsemble.AirPlay.Receiver.Audio;

/// <summary>
/// Restores packet RTP timestamps after ffmpeg has decoded AAC-LC into an
/// unframed PCM byte stream. A missing packet must remain a hole in RTP time,
/// so the scheduler can conceal it without shortening the source timeline.
/// </summary>
internal sealed class AacRtpPcmMapper(Action<uint, byte[]> enqueue, Action flush)
{
    private const int DecodedFrameBytes = AacDecoderPipe.SamplesPerFrame * 4;
    // Over 90 seconds of decoder backlog. Fail explicitly if the decoder
    // stops producing matching output, instead of retaining metadata forever
    // or blocking the input that ffmpeg might need to produce its next block.
    private const int MaxPendingPackets = 4096;

    private readonly object _gate = new();
    private readonly Queue<PacketStamp> _packets = new();
    private readonly byte[] _decodedFrame = new byte[DecodedFrameBytes];
    private int _decodedBytes;
    private uint? _nextRtp;
    private long _generation;

    private readonly record struct PacketStamp(uint Rtp, long Generation);

    /// <summary>
    /// Register before writing this packet to ffmpeg. Reject a duplicate or
    /// late packet before decoding it, so input and output block counts agree.
    /// Called by the single ordered RTP reader.
    /// </summary>
    public bool TryQueuePacket(uint rtp)
    {
        lock (_gate)
        {
            if (_nextRtp is { } next && unchecked((int)(rtp - next)) < 0)
            {
                return false;
            }

            if (_packets.Count >= MaxPendingPackets)
            {
                throw new InvalidDataException("AAC decoder exceeded the pending RTP timestamp limit");
            }

            _packets.Enqueue(new PacketStamp(rtp, _generation));
            _nextRtp = unchecked(rtp + AacDecoderPipe.SamplesPerFrame);
            return true;
        }
    }

    /// <summary>
    /// stdout reads can split a sample or span several AAC blocks. Recover
    /// complete 1024-sample blocks before assigning the corresponding RTP.
    /// </summary>
    public void WriteDecodedPcm(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            while (!pcm.IsEmpty)
            {
                var copied = Math.Min(DecodedFrameBytes - _decodedBytes, pcm.Length);
                pcm[..copied].CopyTo(_decodedFrame.AsSpan(_decodedBytes));
                _decodedBytes += copied;
                pcm = pcm[copied..];
                if (_decodedBytes != DecodedFrameBytes)
                {
                    continue;
                }

                if (!_packets.TryDequeue(out var packet))
                {
                    throw new InvalidDataException("AAC decoder produced PCM without a corresponding RTP packet");
                }

                _decodedBytes = 0;
                if (packet.Generation != _generation)
                {
                    continue;
                }

                for (var offset = 0; offset < DecodedFrameBytes; offset += PcmFrame.CanonicalFrameBytes)
                {
                    var length = Math.Min(PcmFrame.CanonicalFrameBytes, DecodedFrameBytes - offset);
                    var rtp = unchecked(packet.Rtp + (uint)(offset / AudioFormat.Canonical.BlockAlign));
                    enqueue(rtp, _decodedFrame.AsSpan(offset, length).ToArray());
                }
            }
        }
    }

    /// <summary>
    /// Drop pre-flush audio while continuing to consume its decoder output.
    /// Clearing either the packet queue or a partially decoded block would
    /// attach the next packet's timestamp to the wrong PCM forever.
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            _generation++;
            _nextRtp = null;
            flush();
        }
    }
}
