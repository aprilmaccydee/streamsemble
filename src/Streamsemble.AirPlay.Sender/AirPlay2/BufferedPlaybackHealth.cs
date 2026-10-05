namespace Streamsemble.AirPlay.Sender.AirPlay2;

/// <summary>Distinguishes a transient underrun from an expired playback timeline.</summary>
internal sealed class BufferedPlaybackHealth
{
    internal const long ExpiredLeadGraceNanos = 2_000_000_000;
    private long? _expiredSince;

    // A null lead means paused, unanchored or re-anchoring. None of those
    // states is evidence that a running receiver has missed its deadlines.
    public bool ShouldReconnect(double? leadMs, long nowNanos)
    {
        if (leadMs is null or > 0)
        {
            _expiredSince = null;
            return false;
        }

        _expiredSince ??= nowNanos;
        return nowNanos - _expiredSince.Value >= ExpiredLeadGraceNanos;
    }
}
