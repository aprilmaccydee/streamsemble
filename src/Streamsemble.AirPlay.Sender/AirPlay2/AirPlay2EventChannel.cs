using System.Net;
using System.Net.Sockets;
using Claunia.PropertyList;
using Microsoft.Extensions.Logging;
using Streamsemble.AirPlay.Common.Hap;

namespace Streamsemble.AirPlay.Sender.AirPlay2;

/// <summary>
/// The reverse event channel every AirPlay 2 receiver announces in its session
/// SETUP: the receiver connects backwards conceptually — WE dial the port it
/// returned, and then IT sends HAP-encrypted RTSP requests down the socket and
/// expects 200s. Leaving the port unconnected or the requests unanswered is
/// not cosmetic: receivers gate rendering on it and tear down sessions whose
/// event channel goes quiet. Speakers and mirror displays share this exact
/// behavior, which is why this lives outside <see cref="AirPlay2Session"/>.
/// </summary>
public sealed class AirPlay2EventChannel(
    string displayName,
    IPAddress address,
    HapSessionKeys keys,
    ILogger logger) : IDisposable
{
    private TcpClient? _client;

    /// <summary>Invoked when the receiver closes the channel — the session is dying or dead.</summary>
    public Action? OnClosed { get; init; }

    public async Task ConnectAsync(int port, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var client = new TcpClient(address.AddressFamily);
                await client.ConnectAsync(address, port, ct).ConfigureAwait(false);
                _client = client;
                logger.LogInformation("{Name}: event channel connected on :{Port}", displayName, port);
                _ = ReadLoopAsync(client);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug("{Name}: event channel connect attempt {Attempt} failed ({Message})",
                    displayName, attempt + 1, ex.Message);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        logger.LogWarning("{Name}: could not connect event channel on :{Port}; continuing anyway", displayName, port);
    }

    /// <summary>Decrypt, log and acknowledge everything the receiver sends.</summary>
    private async Task ReadLoopAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var readKey = keys.EventsReadKey;
            var writeKey = keys.EventsWriteKey;
            var keyResolved = false;
            ulong readCounter = 0, writeCounter = 0;
            var plaintext = new List<byte>();
            var lengthHeader = new byte[2];

            while (true)
            {
                if (!await ReadExactAsync(stream, lengthHeader).ConfigureAwait(false))
                {
                    logger.LogInformation("{Name}: event channel closed by receiver", displayName);
                    OnClosed?.Invoke();
                    return;
                }

                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(lengthHeader);
                var body = new byte[length + 16];
                if (!await ReadExactAsync(stream, body).ConfigureAwait(false))
                {
                    logger.LogInformation("{Name}: event channel closed mid-frame", displayName);
                    OnClosed?.Invoke();
                    return;
                }

                byte[] plain;
                var nonce = PairingCrypto.CounterNonce(readCounter);
                try
                {
                    plain = PairingCrypto.ChaCha20Poly1305Decrypt(readKey, nonce, body, lengthHeader);
                }
                catch when (!keyResolved)
                {
                    // The HKDF info names are receiver-perspective; some stacks
                    // interpret them the other way. Lock onto whichever works.
                    (readKey, writeKey) = (writeKey, readKey);
                    plain = PairingCrypto.ChaCha20Poly1305Decrypt(readKey, nonce, body, lengthHeader);
                    logger.LogInformation("{Name}: events keys were swapped relative to expectation", displayName);
                }

                keyResolved = true;
                readCounter++;
                plaintext.AddRange(plain);

                while (TryTakeRtspRequest(plaintext, out var requestLine, out var cseq, out var requestBody))
                {
                    var bodyNote = requestBody.Length > 0 && PropertyListParser.Parse(requestBody) is NSDictionary plist
                        ? $" plist: {plist.ToXmlPropertyList()}"
                        : requestBody.Length > 0 ? $" body {requestBody.Length} B" : "";
                    logger.LogInformation("{Name}: event rx: {Line}{Body}", displayName, requestLine, bodyNote);

                    var response = System.Text.Encoding.ASCII.GetBytes(
                        $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/745.83\r\nContent-Length: 0\r\n\r\n");
                    var respHeader = new byte[2];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(respHeader, (ushort)response.Length);
                    var respNonce = PairingCrypto.CounterNonce(writeCounter++);
                    var encrypted = PairingCrypto.ChaCha20Poly1305Encrypt(writeKey, respNonce, response, respHeader);
                    await stream.WriteAsync(respHeader).ConfigureAwait(false);
                    await stream.WriteAsync(encrypted).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Name}: event channel handler ended", displayName);
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    /// <summary>Extracts one complete RTSP/HTTP-style request from the plaintext buffer, if present.</summary>
    private static bool TryTakeRtspRequest(List<byte> buffer, out string requestLine, out string cseq, out byte[] body)
    {
        requestLine = "";
        cseq = "0";
        body = [];

        var bytes = buffer.ToArray();
        var headerEnd = -1;
        for (var i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                headerEnd = i + 4;
                break;
            }
        }

        if (headerEnd < 0)
        {
            return false;
        }

        var headerText = System.Text.Encoding.ASCII.GetString(bytes, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(value, out contentLength);
            }
            else if (name.Equals("CSeq", StringComparison.OrdinalIgnoreCase))
            {
                cseq = value;
            }
        }

        if (bytes.Length < headerEnd + contentLength)
        {
            return false; // body not fully received yet
        }

        requestLine = lines[0];
        body = bytes[headerEnd..(headerEnd + contentLength)];
        buffer.RemoveRange(0, headerEnd + contentLength);
        return true;
    }

    public void Dispose() => _client?.Dispose();
}
