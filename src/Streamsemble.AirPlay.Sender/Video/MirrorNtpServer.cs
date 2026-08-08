using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Streamsemble.Timing.Ptp;

namespace Streamsemble.AirPlay.Sender.Video;

/// <summary>
/// The timing clock a mirror receiver polls us for. Mirroring inverts the audio
/// arrangement: the receiver pulls time from the sender, so the hub has to
/// answer rather than ask.
///
/// It serves the PTP grandmaster, not
/// <see cref="Streamsemble.Timing.IMasterClock"/>, and that is the whole point
/// of it existing separately from
/// <see cref="Streamsemble.Timing.NtpTimingResponder"/>. Every video packet's
/// presentation stamp comes from <c>VideoFrame.TargetNanos</c>, which is
/// grandmaster time; if the clock the TV disciplines to were a different
/// timeline, the picture would land at a consistent, invisible offset from the
/// audio no matter how correct everything else was.
///
/// Wire format is the same 32-byte exchange the audio responder answers:
/// request type 0x52, reply 0x53, with origin/receive/transmit stamps.
/// </summary>
public sealed class MirrorNtpServer(ILogger logger) : IDisposable
{
    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private bool _sawQuery;

    public int Port { get; private set; }

    /// <summary>Queries answered, for the telemetry panel — a receiver that stops asking has stopped rendering.</summary>
    public long QueriesAnswered { get; private set; }

    public void Start(int port = 0)
    {
        if (_socket is not null)
        {
            return;
        }

        _socket = new UdpClient(AddressFamily.InterNetworkV6);
        _socket.Client.DualMode = true;
        _socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_socket, _cts.Token);
        logger.LogInformation("mirror timing server on UDP :{Port} (serving the PTP grandmaster)", Port);
    }

    private async Task RunAsync(UdpClient socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                // An ICMP port-unreachable surfaces here; keep serving.
                if (ex is ObjectDisposedException)
                {
                    return;
                }

                continue;
            }

            var receiveTime = Ntp(PtpReceiverClock.NowNanos);
            var request = received.Buffer;
            if (request.Length < 32 || (request[1] & 0x7F) != 0x52)
            {
                continue;
            }

            if (!_sawQuery)
            {
                _sawQuery = true;
                logger.LogInformation("mirror timing: {Peer} is asking for our clock — the receiver is alive", received.RemoteEndPoint);
            }

            var response = new byte[32];
            response[0] = 0x80;
            response[1] = 0xD3; // 0x53 reply | marker
            request.AsSpan(2, 2).CopyTo(response.AsSpan(2));   // echo the sequence
            request.AsSpan(24, 8).CopyTo(response.AsSpan(8));  // origin = their transmit stamp
            BinaryPrimitives.WriteUInt64BigEndian(response.AsSpan(16), receiveTime);
            BinaryPrimitives.WriteUInt64BigEndian(response.AsSpan(24), Ntp(PtpReceiverClock.NowNanos));

            try
            {
                await socket.SendAsync(response, received.RemoteEndPoint, ct).ConfigureAwait(false);
                QueriesAnswered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "mirror timing reply to {Peer} failed", received.RemoteEndPoint);
            }
        }
    }

    private const long NtpToUnixSeconds = 2_208_988_800L;

    internal static ulong Ntp(long unixNanos)
    {
        var seconds = unixNanos / 1_000_000_000L;
        var nanos = unixNanos % 1_000_000_000L;
        var fraction = (ulong)(((UInt128)nanos << 32) / 1_000_000_000);
        return ((ulong)(seconds + NtpToUnixSeconds) << 32) | (uint)fraction;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _socket?.Dispose();
        _cts?.Dispose();
    }
}
