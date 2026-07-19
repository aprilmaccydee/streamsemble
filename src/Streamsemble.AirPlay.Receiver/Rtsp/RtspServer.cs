using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Streamsemble.AirPlay.Receiver.Rtsp;

/// <summary>One inbound RTSP request (methods arrive HTTP-shaped too: GET /info, POST /pair-setup).</summary>
public sealed record RtspRequest(string Method, string Uri, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public string Path
    {
        get
        {
            // SETUP et al. use absolute rtsp:// URIs; POST/GET use bare paths.
            if (!Uri.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                return Uri;
            }

            var slash = Uri.IndexOf('/', "rtsp://".Length);
            return slash < 0 ? "/" : Uri[slash..];
        }
    }
}

/// <summary>A reply to send; <see cref="UpgradeAfterSend"/> swaps the transport (HAP cipher) once the bytes are out.</summary>
public sealed record RtspReply(int StatusCode = 200, string ReasonPhrase = "OK")
{
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ContentType { get; init; }
    public byte[] Body { get; init; } = [];
    public Func<Stream, Stream>? UpgradeAfterSend { get; init; }

    public static RtspReply Ok() => new();

    public static RtspReply Error(int status, string reason) => new(status, reason);
}

/// <summary>Per-connection RTSP session handler; created by the server for every accepted connection.</summary>
public interface IRtspConnectionHandler : IDisposable
{
    Task<RtspReply> HandleAsync(RtspRequest request, CancellationToken ct);
}

/// <summary>
/// Minimal RTSP/1.0 server: accepts AirPlay sender connections, reads requests
/// (byte-wise headers + Content-Length body, mirroring <c>RtspClient</c>),
/// dispatches them to a per-connection handler, and echoes CSeq on replies.
/// The transport can be upgraded mid-connection to the HAP-encrypted channel.
/// </summary>
public sealed class RtspServer(
    Func<RtspServerConnection, IRtspConnectionHandler> handlerFactory,
    ILogger logger) : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }

    public void Start(int port)
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.IPv6Any, port);
        _listener.Server.DualMode = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_listener, _cts.Token);
        logger.LogInformation("RTSP server listening on :{Port}", Port);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RTSP accept failed");
                continue;
            }

            _ = ServeConnectionAsync(client, ct);
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        var connection = new RtspServerConnection(client);
        using var handler = handlerFactory(connection);
        logger.LogInformation("RTSP connection from {Remote}", remote);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var request = await connection.ReadRequestAsync(ct).ConfigureAwait(false);
                if (request is null)
                {
                    break; // clean close
                }

                logger.LogDebug("RTSP ← {Method} {Path} ({Bytes} B)", request.Method, request.Path, request.Body.Length);
                var reply = await handler.HandleAsync(request, ct).ConfigureAwait(false);
                await connection.SendReplyAsync(request, reply, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // Sender dropped the connection; normal teardown path.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RTSP connection from {Remote} failed", remote);
        }
        finally
        {
            connection.Dispose();
            logger.LogInformation("RTSP connection from {Remote} closed", remote);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _cts?.Dispose();
    }
}

/// <summary>
/// The transport for one accepted RTSP connection. Owns the stream so the
/// session handler can swap it for a <c>HapCipherStream</c> after pairing.
/// </summary>
public sealed class RtspServerConnection : IDisposable
{
    private readonly TcpClient _tcp;
    private Stream _stream;

    internal RtspServerConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        // Snapshot now: session handlers touch these during Dispose, after the
        // socket is gone (the disposed-socket throw silently killed the whole
        // session cleanup path before).
        RemoteAddress = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
        LocalAddress = ((IPEndPoint)tcp.Client.LocalEndPoint!).Address;
    }

    public IPAddress RemoteAddress { get; }

    public IPAddress LocalAddress { get; }

    /// <summary>Reads one request; null on a clean close between requests.</summary>
    public async Task<RtspRequest?> ReadRequestAsync(CancellationToken ct)
    {
        var headerText = await ReadHeadersAsync(ct).ConfigureAwait(false);
        if (headerText is null)
        {
            return null;
        }

        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var parts = lines[0].Split(' ', 3);
        if (parts.Length < 3)
        {
            throw new IOException($"malformed RTSP request line: {lines[0]}");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length) && length > 0)
        {
            body = new byte[length];
            await _stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        }

        return new RtspRequest(parts[0], parts[1], headers, body);
    }

    public async Task SendReplyAsync(RtspRequest request, RtspReply reply, CancellationToken ct)
    {
        var builder = new StringBuilder();
        builder.Append($"RTSP/1.0 {reply.StatusCode} {reply.ReasonPhrase}\r\n");
        if (request.Header("CSeq") is { } cseq)
        {
            builder.Append($"CSeq: {cseq}\r\n");
        }

        builder.Append($"Server: {ReceiverConstants.ServerAgent}\r\n");
        foreach (var (name, value) in reply.Headers)
        {
            builder.Append($"{name}: {value}\r\n");
        }

        if (reply.ContentType is not null)
        {
            builder.Append($"Content-Type: {reply.ContentType}\r\n");
        }

        builder.Append($"Content-Length: {reply.Body.Length}\r\n\r\n");

        await _stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct).ConfigureAwait(false);
        if (reply.Body.Length > 0)
        {
            await _stream.WriteAsync(reply.Body, ct).ConfigureAwait(false);
        }

        await _stream.FlushAsync(ct).ConfigureAwait(false);

        if (reply.UpgradeAfterSend is { } wrap)
        {
            _stream = wrap(_stream);
        }
    }

    /// <summary>Byte-wise header read until CRLFCRLF; null if the connection closes before the first byte.</summary>
    private async Task<string?> ReadHeadersAsync(CancellationToken ct)
    {
        var buffer = new List<byte>(512);
        var single = new byte[1];
        while (true)
        {
            int read;
            try
            {
                read = await _stream.ReadAsync(single, ct).ConfigureAwait(false);
            }
            catch (IOException) when (buffer.Count == 0)
            {
                return null;
            }

            if (read == 0)
            {
                if (buffer.Count == 0)
                {
                    return null;
                }

                throw new IOException("RTSP connection closed mid-request");
            }

            buffer.Add(single[0]);
            if (buffer.Count >= 4
                && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                return Encoding.ASCII.GetString(buffer.ToArray());
            }
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }
}
