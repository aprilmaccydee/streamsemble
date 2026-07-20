namespace Streamsemble.AirPlay.Sender.AirPlay2;

/// <summary>
/// The group's canonical timeline anchor for one uninterrupted playback epoch:
/// "capture-counter sample C renders at grandmaster nanosecond N" (before
/// per-device trim). The first buffered session to anchor establishes it;
/// every later anchor in the same epoch (late join, reconnect) derives its
/// SETRATEANCHORTIME from this mapping instead of from "now + latency".
///
/// Without this, sessions anchor independently against real time, and every
/// source gap since the incumbents anchored (track-change loads, dropped
/// frames) — which their contiguous RTP timeline edits out — becomes debt a
/// fresh joiner doesn't share: it renders ahead of the group by exactly the
/// accumulated gap time. Mapping through the shared capture counter makes
/// relative sync structural; gaps and encoder-age error can shift the whole
/// group, never one speaker against another.
///
/// Reset whenever the whole group re-anchors (pause/skip flush, fresh start);
/// per-session re-anchors must NOT reset it — inheriting the epoch is the
/// entire point.
/// </summary>
public sealed class GroupTimelineAnchor
{
    private readonly object _gate = new();
    private bool _set;
    private long _captureSample;
    private ulong _nanos;

    /// <summary>
    /// Map a proposed anchor onto the epoch. Establishes the epoch with the
    /// proposal when none exists; otherwise returns the epoch-consistent nanos
    /// for the capture sample, plus how far ahead the proposal was (the
    /// source-gap debt the caller is inheriting).
    /// </summary>
    public (ulong Nanos, bool Established, double DebtMs) Map(long captureSample, ulong proposedNanos)
    {
        lock (_gate)
        {
            if (!_set)
            {
                _set = true;
                _captureSample = captureSample;
                _nanos = proposedNanos;
                return (proposedNanos, true, 0.0);
            }

            var mapped = (ulong)((long)_nanos + (captureSample - _captureSample) * 1_000_000_000L / 44100);
            var debtMs = ((long)proposedNanos - (long)mapped) / 1e6;
            return (mapped, false, debtMs);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _set = false;
        }
    }
}
