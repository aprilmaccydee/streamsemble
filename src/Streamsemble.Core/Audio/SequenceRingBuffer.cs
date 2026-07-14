namespace Streamsemble.Core.Audio;

/// <summary>
/// Fixed-capacity buffer of payloads keyed by a monotonically increasing
/// sequence number, used to serve RAOP retransmit requests: the sender keeps
/// the last N RTP packets and re-sends any the speaker reports missing.
/// A slot only answers for the exact sequence it holds, so stale entries that
/// have been overwritten by wraparound are misses, never wrong data.
/// </summary>
public sealed class SequenceRingBuffer(int capacity)
{
    private readonly (long Seq, byte[]? Data)[] _slots = new (long, byte[]?)[capacity];
    private readonly object _gate = new();

    public int Capacity { get; } = capacity;

    public void Add(long seq, byte[] data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seq);
        lock (_gate)
        {
            _slots[seq % Capacity] = (seq, data);
        }
    }

    public bool TryGet(long seq, out byte[] data)
    {
        lock (_gate)
        {
            var (storedSeq, stored) = _slots[seq % Capacity];
            if (stored is not null && storedSeq == seq)
            {
                data = stored;
                return true;
            }
        }

        data = [];
        return false;
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_slots);
        }
    }
}
