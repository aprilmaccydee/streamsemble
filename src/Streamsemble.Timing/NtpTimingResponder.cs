using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Streamsemble.Timing;

/// <summary>
/// Answers RAOP/AirPlay NTP-style timing queries from speakers. Each device
/// continuously measures its offset to us through these exchanges and
/// disciplines its playback clock to the master clock; because one responder
/// (and one clock) serves every session, all speakers converge on the same
/// timeline.
///
/// Wire format (32 bytes, RTP-ish): type 0x52 request / 0x53 response;
/// [8..16) origin, [16..24) receive, [24..32) transmit timestamps in 64-bit
/// NTP format.
/// </summary>
public sealed class NtpTimingResponder(IMasterClock clock, ILogger<NtpTimingResponder> logger) : IDisposable
{
    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private bool _sawQuery;

    public int Port { get; private set; }

    public void Start(int port = 0)
    {
        if (_socket is not null)
        {
            return;
        }

        // Dual-stack: HomePods and IPv6-bound receivers query over v6.
        _socket = new UdpClient(AddressFamily.InterNetworkV6);
        _socket.Client.DualMode = true;
        _socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_socket, _cts.Token);
        logger.LogInformation("NTP timing responder listening on UDP {Port}", Port);
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
            catch when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // e.g. ICMP port-unreachable surfaced on the socket
            }

            var receiveTime = clock.NowNtp;
            var request = received.Buffer;
            if (!_sawQuery)
            {
                _sawQuery = true;
                logger.LogInformation("NTP timing: first query received from {Peer} — reverse path is open", received.RemoteEndPoint);
            }

            if (request.Length < 32 || (request[1] & 0x7f) != 0x52)
            {
                continue;
            }

            var response = new byte[32];
            response[0] = 0x80;
            response[1] = 0xd3; // 0x53 | marker
            request.AsSpan(2, 2).CopyTo(response.AsSpan(2)); // echo sequence
            request.AsSpan(24, 8).CopyTo(response.AsSpan(8)); // origin = requester's transmit time
            BinaryPrimitives.WriteUInt64BigEndian(response.AsSpan(16), receiveTime);
            BinaryPrimitives.WriteUInt64BigEndian(response.AsSpan(24), clock.NowNtp);

            try
            {
                await socket.SendAsync(response, received.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to answer timing query from {Peer}", received.RemoteEndPoint);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _socket?.Dispose();
    }
}
