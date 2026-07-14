using System.Buffers.Binary;

namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// The HAP encrypted session layer that wraps an RTSP <see cref="Stream"/>
/// after pair-verify. Each direction has its own 32-byte key and its own
/// 64-bit packet counter. A frame is: 2-byte little-endian plaintext length
/// (also the AEAD additional data) ‖ ChaCha20-Poly1305 ciphertext ‖ 16-byte
/// tag. The nonce is the counter as 8 little-endian bytes in the low half of a
/// 12-byte block; the counter increments once per frame, per direction.
/// </summary>
public sealed class HapCipherStream(Stream inner, byte[] readKey, byte[] writeKey) : Stream
{
    private const int MaxFrame = 0x400; // HAP caps plaintext frames at 1024 bytes
    private readonly byte[] _readKey = readKey;
    private readonly byte[] _writeKey = writeKey;
    private ulong _readCounter;
    private ulong _writeCounter;
    private byte[] _readBuffer = [];
    private int _readOffset;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_readOffset >= _readBuffer.Length)
        {
            _readBuffer = await ReadFrameAsync(ct).ConfigureAwait(false);
            _readOffset = 0;
            if (_readBuffer.Length == 0)
            {
                return 0; // EOF
            }
        }

        var n = Math.Min(buffer.Length, _readBuffer.Length - _readOffset);
        _readBuffer.AsMemory(_readOffset, n).CopyTo(buffer);
        _readOffset += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(header, ct).ConfigureAwait(false))
        {
            return [];
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var body = new byte[length + 16];
        if (!await ReadExactAsync(body, ct).ConfigureAwait(false))
        {
            return [];
        }

        var nonce = PairingCrypto.CounterNonce(_readCounter++);
        return PairingCrypto.ChaCha20Poly1305Decrypt(_readKey, nonce, body, header);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await inner.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var chunk = Math.Min(MaxFrame, buffer.Length - offset);
            var header = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)chunk);
            var nonce = PairingCrypto.CounterNonce(_writeCounter++);
            var ciphertext = PairingCrypto.ChaCha20Poly1305Encrypt(_writeKey, nonce, buffer.Slice(offset, chunk).ToArray(), header);

            await inner.WriteAsync(header, ct).ConfigureAwait(false);
            await inner.WriteAsync(ciphertext, ct).ConfigureAwait(false);
            offset += chunk;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
