using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Streamsemble.AirPlay.Common.Hap;

namespace Streamsemble.AirPlay.Receiver.Audio;

/// <summary>One decrypted buffered-audio packet: an AAC-LC frame stamped with its RTP time.</summary>
public readonly record struct BufferedAudioPacket(uint Sequence, uint RtpTime, byte[] Frame);

/// <summary>
/// The receiver side of the AirPlay 2 buffered (type 103) data channel: a TCP
/// listener whose port goes in the stream SETUP reply. Parses the framed
/// packets (u16be self-inclusive length ‖ 12-byte header ‖ ChaCha20-Poly1305
/// ciphertext+tag ‖ 8-byte LE-counter nonce; AAD = header[4..12); key = the
/// shk from SETUP phase 2) and hands decrypted AAC frames to the consumer.
/// Legacy u32-length framing (pre-370 senders) is sniffed automatically: a
/// modern u16 length for any real packet size never starts 0x00 0x00.
/// </summary>
public sealed class BufferedAudioServer(byte[] audioKey, ILogger logger) : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }

    /// <summary>Called for every decrypted audio packet, in arrival order, from the socket read loop — backpressure by blocking.</summary>
    public required Func<BufferedAudioPacket, CancellationToken, ValueTask> OnPacket { get; init; }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.IPv6Any, 0);
        _listener.Server.DualMode = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync(_listener, _cts.Token);
        logger.LogInformation("buffered audio listener on :{Port}", Port);
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                logger.LogInformation("buffered audio connection from {Remote}", client.Client.RemoteEndPoint);
                await ReadPacketsAsync(client.GetStream(), ct).ConfigureAwait(false);
                logger.LogInformation("buffered audio connection closed");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "buffered audio channel failed");
        }
    }

    private async Task ReadPacketsAsync(Stream stream, CancellationToken ct)
    {
        var lengthProbe = new byte[2];
        var packets = 0L;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await stream.ReadExactlyAsync(lengthProbe, ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            int packetLength; // self-inclusive on the wire
            int lengthBytes;
            if (lengthProbe[0] == 0 && lengthProbe[1] == 0)
            {
                // Legacy u32be length: read the remaining two length bytes.
                var rest = new byte[2];
                await stream.ReadExactlyAsync(rest, ct).ConfigureAwait(false);
                packetLength = rest[0] << 8 | rest[1];
                lengthBytes = 4;
            }
            else
            {
                packetLength = BinaryPrimitives.ReadUInt16BigEndian(lengthProbe);
                lengthBytes = 2;
            }

            var remaining = packetLength - lengthBytes;
            if (remaining < 12 + 16 + 8)
            {
                // Header + tag + nonce is the minimum (primer packets have an empty payload).
                logger.LogWarning("buffered packet too short ({Length} B); dropping connection", packetLength);
                return;
            }

            var packet = new byte[remaining];
            await stream.ReadExactlyAsync(packet, ct).ConfigureAwait(false);

            var header = packet[..12];
            var nonceTail = packet[(remaining - 8)..];
            var ciphertext = packet[12..(remaining - 8)];

            uint sequence, rtpTime;
            if (lengthBytes == 2 && header[0] == 0x80)
            {
                sequence = BinaryPrimitives.ReadUInt32BigEndian(header) & 0x7FFFFF;
                rtpTime = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            }
            else
            {
                // Legacy: word0 is zero, ts at +4, sample rate at +8.
                sequence = (uint)packets;
                rtpTime = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            }

            byte[] frame;
            try
            {
                frame = PairingCrypto.ChaCha20Poly1305Decrypt(
                    audioKey, PairingCrypto.Nonce(nonceTail), ciphertext, header[4..12]);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "buffered packet decrypt failed (seq {Seq}); dropping connection", sequence);
                return;
            }

            packets++;
            if (frame.Length == 0)
            {
                continue; // primer packet — announces the stream, carries no audio
            }

            await OnPacket(new BufferedAudioPacket(sequence, rtpTime, frame), ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _cts?.Dispose();
    }
}
