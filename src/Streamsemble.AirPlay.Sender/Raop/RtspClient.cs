using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Streamsemble.AirPlay.Sender.Raop;

public sealed record RtspResponse(int StatusCode, string ReasonPhrase, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// Minimal RTSP/1.0 client for the RAOP handshake. One instance per speaker
/// session; requests are serialized on the single TCP connection.
/// </summary>
public sealed class RtspClient : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _logger;
    private TcpClient? _tcp;
    private Stream? _stream;
    private int _cseq;

    public string Uri { get; set; } = "*";
    public Dictionary<string, string> DefaultHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RtspClient(ILogger logger) => _logger = logger;

    public System.Net.IPAddress? LocalAddress { get; private set; }

    public async Task ConnectAsync(System.Net.IPAddress address, int port, CancellationToken ct)
    {
        _tcp = new TcpClient(address.AddressFamily);
        await _tcp.ConnectAsync(address, port, ct).ConfigureAwait(false);
        _tcp.NoDelay = true;
        _stream = _tcp.GetStream();
        LocalAddress = ((System.Net.IPEndPoint)_tcp.Client.LocalEndPoint!).Address;
    }

    /// <summary>
    /// Replaces the transport with an encrypted wrapper after HAP pair-verify,
    /// so subsequent RTSP requests ride the ChaCha20 control channel. Serialized
    /// against in-flight requests.
    /// </summary>
    public async Task UpgradeStreamAsync(Func<Stream, Stream> wrap, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _stream = wrap(_stream ?? throw new InvalidOperationException("Not connected"));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<RtspResponse> RequestAsync(
        string method,
        CancellationToken ct,
        string? contentType = null,
        byte[]? body = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        string? uriOverride = null)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected");
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var builder = new StringBuilder();
            builder.Append($"{method} {uriOverride ?? Uri} RTSP/1.0\r\n");
            builder.Append($"CSeq: {++_cseq}\r\n");
            foreach (var (name, value) in DefaultHeaders)
            {
                builder.Append($"{name}: {value}\r\n");
            }

            if (extraHeaders is not null)
            {
                foreach (var (name, value) in extraHeaders)
                {
                    builder.Append($"{name}: {value}\r\n");
                }
            }

            if (contentType is not null)
            {
                builder.Append($"Content-Type: {contentType}\r\n");
            }

            builder.Append($"Content-Length: {body?.Length ?? 0}\r\n\r\n");
            _logger.LogTrace("RTSP → {Method} {Uri} (CSeq {CSeq})", method, Uri, _cseq);

            await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct).ConfigureAwait(false);
            if (body is { Length: > 0 })
            {
                await stream.WriteAsync(body, ct).ConfigureAwait(false);
            }

            var response = await ReadResponseAsync(stream, ct).ConfigureAwait(false);
            _logger.LogTrace("RTSP ← {Status} {Reason}", response.StatusCode, response.ReasonPhrase);
            return response;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<RtspResponse> ReadResponseAsync(Stream stream, CancellationToken ct)
    {
        var headerText = await ReadHeadersAsync(stream, ct).ConfigureAwait(false);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var statusParts = lines[0].Split(' ', 3);
        var statusCode = int.Parse(statusParts[1]);
        var reason = statusParts.Length > 2 ? statusParts[2] : "";

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
            await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        }

        return new RtspResponse(statusCode, reason, headers, body);
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        // Read byte-wise until CRLFCRLF; header blocks are tiny and this keeps
        // us from over-reading into the body.
        var buffer = new List<byte>(512);
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single, ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("RTSP connection closed while reading response headers");
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
        _stream?.Dispose();
        _tcp?.Dispose();
        _lock.Dispose();
    }
}
