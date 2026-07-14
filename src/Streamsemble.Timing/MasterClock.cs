using System.Diagnostics;

namespace Streamsemble.Timing;

/// <summary>
/// The single clock every outbound speaker session is disciplined to. All
/// sender-side timestamps (RTP pacing, sync packets, NTP timing responses)
/// must come from one instance so that N speakers share one timeline —
/// this is the foundation of multi-room sync.
/// </summary>
public interface IMasterClock
{
    /// <summary>Current time in 64-bit NTP format (seconds since 1900 in the top 32 bits, fraction below).</summary>
    ulong NowNtp { get; }

    /// <summary>Monotonic seconds, same timeline as <see cref="NowNtp"/>.</summary>
    double NowSeconds { get; }

    /// <summary>NTP timestamp for a moment <paramref name="seconds"/> in the future (e.g. render anchor).</summary>
    ulong NtpInSeconds(double seconds);
}

public sealed class MasterClock : IMasterClock
{
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Wall time is sampled once at startup only to give NTP values a sane
    // absolute base; from then on the clock advances purely on Stopwatch so
    // it can never step backwards (devices sync to us, not to true time).
    private readonly double _baseNtpSeconds = (DateTime.UtcNow - NtpEpoch).TotalSeconds;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public double NowSeconds => _baseNtpSeconds + _stopwatch.Elapsed.TotalSeconds;

    public ulong NowNtp => ToNtp(NowSeconds);

    public ulong NtpInSeconds(double seconds) => ToNtp(NowSeconds + seconds);

    public static ulong ToNtp(double seconds)
    {
        var whole = (ulong)seconds;
        var fraction = (ulong)((seconds - whole) * 4294967296.0);
        return (whole << 32) | fraction;
    }
}
