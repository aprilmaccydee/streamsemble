using System.Buffers.Binary;

namespace Streamsemble.AirPlay.Tests;

internal static class AacEncoderTestStreams
{
    public static byte[] Frames(int count)
    {
        // The encoder pipe parses framing, not AAC payload contents. Four
        // payload bytes identify each AU so an omitted oldest frame is visible.
        var data = new byte[count * 11];
        for (var i = 0; i < count; i++)
        {
            var frame = data.AsSpan(i * 11, 11);
            new byte[] { 0xff, 0xf1, 0x50, 0x80, 0x01, 0x7f, 0xfc }.CopyTo(frame);
            BinaryPrimitives.WriteInt32LittleEndian(frame[7..], i);
        }

        return data;
    }

    internal sealed class HoldAfterStream(byte[] prefix) : MemoryStream(prefix)
    {
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length)
            {
                return await base.ReadAsync(buffer, cancellationToken);
            }

            Drained.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
