using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Streamsemble.Timing.Ptp;

/// <summary>
/// Single owner of UDP 319/320 for the whole process. The hub runs two PTP
/// roles at once — the receiver-side grandmaster serving inbound AirPlay
/// senders (phones/Macs), and the sender-side engine syncing with the speaker
/// group — and both used to bind the same ports with SO_REUSEADDR. The kernel
/// then distributes inbound unicast across the duplicate sockets by flow
/// hash, so each remote peer randomly reaches one role or the other (first
/// hub test: the TV's flow hashed to the engine and played; the Kitchen
/// Sonos's hashed to the receiver clock, never synced, and 400'd every
/// SETRATEANCHORTIME). The mux routes each inbound packet by source address —
/// receiver-session peers to the receiver clock, everything else to the
/// engine — and both roles send through the shared sockets.
/// </summary>
public sealed class PtpPortMux
{
    public readonly record struct PtpPacket(byte[] Data, IPAddress From, long ReceivedNanos);

    /// <summary>One role's inbound view of the two ports.</summary>
    public sealed class Consumer
    {
        internal Consumer()
        {
            Event = Channel.CreateUnbounded<PtpPacket>(new UnboundedChannelOptions { SingleWriter = true });
            General = Channel.CreateUnbounded<PtpPacket>(new UnboundedChannelOptions { SingleWriter = true });
        }

        public Channel<PtpPacket> Event { get; }
        public Channel<PtpPacket> General { get; }
    }

    private static readonly object SharedGate = new();
    private static PtpPortMux? _shared;

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private Socket? _event;   // UDP 319
    private Socket? _general; // UDP 320
    private CancellationTokenSource? _cts;
    private bool _bound;
    private bool _failed;

    private Consumer? _receiver;
    private Func<IPAddress, bool>? _receiverOwnsPeer;
    private Consumer? _engine;

    private PtpPortMux(ILogger logger) => _logger = logger;

    /// <summary>The process-wide instance (the ports are exclusive per host).</summary>
    public static PtpPortMux GetShared(ILogger logger)
    {
        lock (SharedGate)
        {
            return _shared ??= new PtpPortMux(logger);
        }
    }

    /// <summary>Binds 319/320 (idempotent) and starts routing. False if the ports can't be bound.</summary>
    public bool EnsureBound()
    {
        lock (_gate)
        {
            if (_bound)
            {
                return true;
            }

            if (_failed)
            {
                return false;
            }

            try
            {
                _event = Bind(PtpWire.EventPort);
                _general = Bind(PtpWire.GeneralPort);
            }
            catch (Exception ex)
            {
                _failed = true;
                _event?.Dispose();
                _event = null;
                _logger.LogError(ex,
                    "PTP mux: cannot bind UDP {E}/{G} — no timing role can run " +
                    "(ports in use by another PTP daemon, or privileged on this OS?)",
                    PtpWire.EventPort, PtpWire.GeneralPort);
                return false;
            }

            _cts = new CancellationTokenSource();
            _ = RxLoopAsync(_event!, isEvent: true, _cts.Token);
            _ = RxLoopAsync(_general!, isEvent: false, _cts.Token);
            _bound = true;
            _logger.LogInformation("PTP mux bound on :{E}/:{G}", PtpWire.EventPort, PtpWire.GeneralPort);
            return true;
        }
    }

    private static Socket Bind(int port)
    {
        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        socket.DualMode = true;
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        return socket;
    }

    /// <summary>Receiver-clock role: gets packets whose source it claims via <paramref name="ownsPeer"/>.</summary>
    public Consumer RegisterReceiverRole(Func<IPAddress, bool> ownsPeer)
    {
        lock (_gate)
        {
            _receiver = new Consumer();
            _receiverOwnsPeer = ownsPeer;
            return _receiver;
        }
    }

    /// <summary>Sender-engine role: gets every packet the receiver role doesn't claim. Re-registering replaces the previous engine.</summary>
    public Consumer RegisterEngineRole()
    {
        lock (_gate)
        {
            _engine?.Event.Writer.TryComplete();
            _engine?.General.Writer.TryComplete();
            _engine = new Consumer();
            return _engine;
        }
    }

    public void UnregisterEngineRole(Consumer consumer)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_engine, consumer))
            {
                _engine = null;
            }
        }

        consumer.Event.Writer.TryComplete();
        consumer.General.Writer.TryComplete();
    }

    public Task SendEventAsync(byte[] data, IPEndPoint dest, CancellationToken ct)
        => SendAsync(_event, data, dest, ct);

    public Task SendGeneralAsync(byte[] data, IPEndPoint dest, CancellationToken ct)
        => SendAsync(_general, data, dest, ct);

    private static async Task SendAsync(Socket? socket, byte[] data, IPEndPoint dest, CancellationToken ct)
    {
        if (socket is null)
        {
            throw new InvalidOperationException("PTP mux is not bound");
        }

        await socket.SendToAsync(data, dest, ct).ConfigureAwait(false);
    }

    private async Task RxLoopAsync(Socket socket, bool isEvent, CancellationToken ct)
    {
        var buf = new byte[512];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buf, new IPEndPoint(IPAddress.IPv6Any, 0), ct).ConfigureAwait(false);
            }
            catch when (ct.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }

            var received = PtpEngine.NowUnixNanos();
            var from = Normalize(((IPEndPoint)result.RemoteEndPoint).Address);
            var packet = new PtpPacket(buf.AsSpan(0, result.ReceivedBytes).ToArray(), from, received);

            Consumer? target;
            lock (_gate)
            {
                target = _receiverOwnsPeer?.Invoke(from) == true && _receiver is not null
                    ? _receiver
                    : _engine ?? _receiver;
            }

            (isEvent ? target?.Event : target?.General)?.Writer.TryWrite(packet);
        }
    }

    /// <summary>v4-mapped v6 → plain v4 so filters and engine master comparisons see one form.</summary>
    public static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
