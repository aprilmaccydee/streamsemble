using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Streamsemble.AirPlay.Sender.Raop;

/// <summary>
/// The group's shared RAOP control socket. Outbound: the per-second sync
/// packets (type 0x54) that anchor every speaker to the same
/// RTP-time↔master-clock mapping — the heart of multi-room alignment.
/// Inbound: retransmit requests (type 0x55), answered from the packet ring
/// as type 0x56.
/// </summary>
public sealed class ControlChannel(ILogger logger) : IDisposable
{
    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private bool _sawPacket;
    private Func<ushort, IPEndPoint, byte[]?>? _retransmitLookup;

    public int Port { get; private set; }

    /// <param name="retransmitLookup">Returns the original RTP packet for (sequence, requesting endpoint), or null if it left the ring.</param>
    public void Start(int port, Func<ushort, IPEndPoint, byte[]?> retransmitLookup)
    {
        _retransmitLookup = retransmitLookup;
        _socket = new UdpClient(AddressFamily.InterNetworkV6);
        _socket.Client.DualMode = true;
        _socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        _cts = new CancellationTokenSource();
        _ = ReceiveLoopAsync(_socket, _cts.Token);
        logger.LogInformation("RAOP control channel on UDP {Port}", Port);
    }

    /// <summary>
    /// Sends one sync packet anchoring playback: "RTP timestamp <paramref name="rtpTimestamp"/>
    /// renders at NTP time <paramref name="renderNtp"/>". The receiver derives all
    /// other packets' play times from this. Both RTP fields carry the same value
    /// (per OwnTone/airplay2-rs). <paramref name="first"/> marks the anchor (0x90).
    /// </summary>
    public async ValueTask SendSyncAsync(IPEndPoint controlEndpoint, uint rtpTimestamp, ulong renderNtp, bool first, CancellationToken ct)
    {
        var socket = _socket ?? throw new InvalidOperationException("Control channel not started");
        var packet = new byte[20];
        packet[0] = (byte)(first ? 0x90 : 0x80);
        packet[1] = 0xd4; // 0x54 | marker
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x0007);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), rtpTimestamp);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), renderNtp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), rtpTimestamp);
        try
        {
            await socket.SendAsync(packet, controlEndpoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Sync send to {Endpoint} failed", controlEndpoint);
        }
    }

    private ushort _ptpSyncSeq;

    /// <summary>
    /// PTP-mode audio sync (payload type 87, 28 bytes): "RTP timestamp
    /// <paramref name="rtpTimestamp"/> renders at PTP grandmaster time
    /// <paramref name="ptpClockNanos"/>", carrying the grandmaster clock id so
    /// the receiver ties our stream to the PTP clock it just became master of.
    /// </summary>
    public async ValueTask SendPtpSyncAsync(IPEndPoint controlEndpoint, uint rtpTimestamp, ulong ptpClockNanos, uint nextRtp, byte[] masterClockId, bool first, CancellationToken ct)
    {
        var socket = _socket ?? throw new InvalidOperationException("Control channel not started");
        var packet = new byte[28];
        packet[0] = (byte)(first ? 0x90 : 0x80);
        packet[1] = 0xd7; // 0x57 | marker
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), _ptpSyncSeq++);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), rtpTimestamp);
        var ptpSecs = (uint)(ptpClockNanos / 1_000_000_000UL);
        var ptpNanos = ptpClockNanos % 1_000_000_000UL;
        var ptpFrac = (uint)((ptpNanos << 32) / 1_000_000_000UL);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ptpSecs);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), ptpFrac);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), nextRtp);
        masterClockId.CopyTo(packet.AsSpan(20));
        if (_ptpSyncSeq <= 2)
        {
            logger.LogInformation(
                "DIAG PTP sync #{Seq}: dest={Dest}, rtp_ts={Rtp}, ptp_secs={Secs}, ptp_frac={Frac}, clock_id={Clock}, hex={Hex}",
                _ptpSyncSeq - 1, controlEndpoint, rtpTimestamp, ptpSecs, ptpFrac, Convert.ToHexString(masterClockId), Convert.ToHexString(packet));
        }
        try
        {
            await socket.SendAsync(packet, controlEndpoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "PTP sync send to {Endpoint} failed", controlEndpoint);
        }
    }

    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            var data = received.Buffer;
            if (!_sawPacket)
            {
                _sawPacket = true;
                var type = data.Length > 1 ? data[1] & 0x7f : -1;
                logger.LogInformation("Control channel: first packet from {Peer}, type 0x{Type:x2} ({Len} bytes) — 0x55=retransmit-request", received.RemoteEndPoint, type, data.Length);
            }

            if (data.Length < 8 || (data[1] & 0x7f) != 0x55)
            {
                continue;
            }

            var firstSeq = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
            var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
            logger.LogDebug("{Peer} requested retransmit of {Count} from seq {Seq}", received.RemoteEndPoint, count, firstSeq);

            for (var i = 0; i < count; i++)
            {
                var seq = (ushort)(firstSeq + i);
                if (_retransmitLookup?.Invoke(seq, received.RemoteEndPoint) is not { } original)
                {
                    continue;
                }

                var resend = new byte[4 + original.Length];
                resend[0] = 0x80;
                resend[1] = 0xd6; // 0x56 | marker
                BinaryPrimitives.WriteUInt16BigEndian(resend.AsSpan(2), 1);
                original.CopyTo(resend, 4);
                try
                {
                    await socket.SendAsync(resend, received.RemoteEndPoint, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Retransmit to {Endpoint} failed", received.RemoteEndPoint);
                }
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _socket?.Dispose();
    }
}
