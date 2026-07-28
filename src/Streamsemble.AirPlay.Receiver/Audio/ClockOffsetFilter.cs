namespace Streamsemble.AirPlay.Receiver.Audio;

/// <summary>
/// Sender-clock → local-clock translation built from anchor-packet arrival
/// times. A 0xD7's network time is the instant the packet left the sender,
/// on the SENDER's PTP timeline — empirically its monotonic clock (~hours-
/// since-boot values), NOT our grandmaster time, so the two can never be
/// compared raw. Each arrival gives one sample of
/// (local now − sender send time) = true offset + network/stack delay;
/// the minimum over a sliding window converges on the offset plus the
/// path's floor delay (sub-ms on a LAN). Same min-filter idea the
/// sender-side PTP discipline uses. The window slides so slow relative
/// clock drift keeps being tracked.
/// </summary>
internal sealed class ClockOffsetFilter(int window = 16)
{
    private readonly long[] _samples = new long[window];
    private int _count;
    private int _next;

    /// <summary>Feed one offset sample; returns the current windowed minimum.</summary>
    public long Update(long offsetSample)
    {
        _samples[_next] = offsetSample;
        _next = (_next + 1) % _samples.Length;
        _count = Math.Min(_count + 1, _samples.Length);

        var min = long.MaxValue;
        for (var i = 0; i < _count; i++)
        {
            min = Math.Min(min, _samples[i]);
        }

        return min;
    }
}
