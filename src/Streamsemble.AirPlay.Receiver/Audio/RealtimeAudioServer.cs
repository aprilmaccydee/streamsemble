using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Streamsemble.AirPlay.Common.Hap;
using Streamsemble.Timing.Ptp;

namespace Streamsemble.AirPlay.Receiver.Audio;

/// <summary>One decrypted realtime packet: an ALAC frame with its RTP stamps.</summary>
public readonly record struct RealtimeAudioPacket(ushort Sequence, uint RtpTime, byte[] Frame);

/// <summary>
/// The receiver side of the AirPlay 2 realtime (type 96) stream — what macOS
/// system output uses. Two UDP sockets whose ports go in the stream SETUP
/// reply: data carries RTP packets (12-byte header ‖ ChaCha20-Poly1305
/// ciphertext+tag ‖ 8-byte LE-counter nonce; AAD = header[4..12); key = shk —
/// the same envelope as buffered, per-packet), control carries 0xD7 anchor
/// packets — parsed and surfaced via <see cref="OnAnchor"/>, they are the
/// stream's only statement of when frames should RENDER (the sender
/// transmits well ahead of real time) — and 0xD6 retransmit payloads
/// (ignored: we never request resends). Packets are re-ordered on a small
/// window keyed by the RTP sequence; anything that hasn't arrived by the
/// time the window slides past it is a dropped-frame glitch, exactly like a
/// lossy speaker.
/// </summary>
public sealed class RealtimeAudioServer(byte[] audioKey, ILogger logger) : IDisposable
{
    private const int ReorderWindow = 16;

    private UdpClient? _data;
    private UdpClient? _control;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<ushort, RealtimeAudioPacket> _pending = [];
    private ushort _expectedSeq;
    private bool _started;

    public int DataPort { get; private set; }

    public int ControlPort { get; private set; }

    /// <summary>Called with decrypted packets in sequence order, from the socket read loop.</summary>
    public required Func<RealtimeAudioPacket, CancellationToken, ValueTask> OnPacket { get; init; }

    /// <summary>
    /// Called per 0xD7 anchor: RTP frame <c>frame</c> is audible at LOCAL
    /// grandmaster reading <c>nanos</c> — the packet's sender-clock time
    /// already translated through <see cref="ClockOffsetFilter"/>. Sent
    /// ~1/s; the first one arrives with (or just before) the first audio
    /// packets.
    /// </summary>
    public Action<uint, long>? OnAnchor { get; init; }

    private readonly ClockOffsetFilter _senderClockOffset = new();

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _data = BindDualMode();
        _control = BindDualMode();
        DataPort = ((IPEndPoint)_data.Client.LocalEndPoint!).Port;
        ControlPort = ((IPEndPoint)_control.Client.LocalEndPoint!).Port;
        _ = ReadDataAsync(_data, _cts.Token);
        _ = ReadControlAsync(_control, _cts.Token);
        logger.LogInformation("realtime audio listeners: data :{Data}, control :{Control}", DataPort, ControlPort);
    }

    private static UdpClient BindDualMode()
    {
        var udp = new UdpClient(AddressFamily.InterNetworkV6);
        udp.Client.DualMode = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return udp;
    }

    private async Task ReadDataAsync(UdpClient udp, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var packet = result.Buffer;
                if (packet.Length < 12 + 16 + 8 || (packet[0] & 0x80) == 0)
                {
                    continue;
                }

                var seq = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
                var rtpTime = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));

                byte[] frame;
                try
                {
                    frame = PairingCrypto.ChaCha20Poly1305Decrypt(
                        audioKey,
                        PairingCrypto.Nonce(packet[^8..]),
                        packet[12..^8],
                        packet[4..12]);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "realtime packet decrypt failed (seq {Seq})", seq);
                    continue;
                }

                await DeliverInOrderAsync(new RealtimeAudioPacket(seq, rtpTime, frame), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "realtime data channel failed");
        }
    }

    private async ValueTask DeliverInOrderAsync(RealtimeAudioPacket packet, CancellationToken ct)
    {
        if (!_started)
        {
            _started = true;
            _expectedSeq = packet.Sequence;
        }

        // Late duplicate of something we already played: drop.
        var behind = (ushort)(_expectedSeq - packet.Sequence);
        if (behind is > 0 and < ReorderWindow * 4)
        {
            return;
        }

        _pending[packet.Sequence] = packet;

        // Emit the contiguous run; if the window backs up, skip the hole.
        while (_pending.Count > 0)
        {
            if (_pending.Remove(_expectedSeq, out var next))
            {
                await OnPacket(next, ct).ConfigureAwait(false);
                _expectedSeq++;
            }
            else if (_pending.Count >= ReorderWindow)
            {
                logger.LogDebug("realtime packet {Seq} lost (window full); skipping", _expectedSeq);
                _expectedSeq++;
            }
            else
            {
                break;
            }
        }
    }

    private async Task ReadControlAsync(UdpClient udp, CancellationToken ct)
    {
        try
        {
            var anchorsSeen = 0;
            while (!ct.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var packet = result.Buffer;
                var type = packet.Length >= 2 ? packet[1] & 0x7F : 0;
                // 0x57 (0xD7) = anchor: [4..8) frame F (u32 BE) audible at
                // [8..16) T (u64 BE), [16..20) the frame being TRANSMITTED at
                // T (u32 BE; F + ~77175 = the sender's transmission lead),
                // [20..28) clock id (layout per shairport-sync's
                // rtp_ap2_control_receiver). T is on the SENDER's timeline
                // (its monotonic clock — hours-since-boot scale), and T is
                // also this packet's send instant (one instant, two cursor
                // readings), which is what makes arrival-based offset
                // filtering sound. 0x56 (0xD6) = retransmit payload, ignored.
                if (type == 0x57 && packet.Length >= 28)
                {
                    var frame = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));
                    var senderNanos = (long)BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(8));
                    var offset = _senderClockOffset.Update(PtpReceiverClock.NowNanos - senderNanos);
                    var localNanos = senderNanos + offset;
                    if (anchorsSeen++ == 0)
                    {
                        var txFrame = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16));
                        logger.LogInformation(
                            "realtime 0xD7 anchor: frame {Frame} audible at sender ns {Sender} = local ns {Local} "
                            + "(clock offset {OffsetS:F3} s, sender transmission lead {LeadMs:F0} ms)",
                            frame, senderNanos, localNanos, offset / 1e9, (uint)(txFrame - frame) * 1000.0 / 44100);
                    }

                    OnAnchor?.Invoke(frame, localNanos);
                }
                else
                {
                    logger.LogTrace("realtime control packet type 0x{Type:X2} ({Len} B)", type, packet.Length);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _data?.Dispose();
        _control?.Dispose();
        _cts?.Dispose();
    }
}
