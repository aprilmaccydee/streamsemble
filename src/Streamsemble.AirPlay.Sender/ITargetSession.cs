using System.Net;
using Streamsemble.Core.Metadata;

namespace Streamsemble.AirPlay.Sender;

/// <summary>
/// A live snapshot of one session's transport/timeline state, read by the web
/// API for display. Nullable fields don't apply to that session's mode (e.g.
/// anchor state on a realtime RAOP session).
/// </summary>
public sealed record SessionTelemetry(
    string Protocol,
    string Mode,
    string? Pairing,
    bool Alive,
    int LatencyTrimMs,
    double ReportedLatencyMs,
    bool? Anchored,
    double? BufferAheadMs,
    double? EncoderAgeMs,
    double? InheritedDebtMs,
    long BufferedPacketsSent,
    string? TimelineId);

/// <summary>
/// A single speaker session in a fan-out group, abstracting over classic RAOP
/// (AES-CBC audio) and HAP-paired AirPlay 2 (ChaCha20 audio). The group builds
/// one plain RTP packet per frame from the shared clock and each session turns
/// it into its own wire bytes, so all targets stay on one timeline.
/// </summary>
public interface ITargetSession : IDisposable
{
    string DisplayName { get; }
    IPEndPoint AudioEndpoint { get; }
    IPEndPoint ControlEndpoint { get; }
    int LatencyTrimMs { get; }

    /// <summary>Speaker address, used to target the PTP grandmaster negotiation.</summary>
    IPAddress DeviceAddress { get; }

    /// <summary>True when the speaker requires gPTP timing (e.g. Sonos) rather than NTP.</summary>
    bool RequiresPtp { get; }

    /// <summary>
    /// False once the session has observed its own death (event channel
    /// closed, keepalives failing, audio transport gone) — the group's health
    /// loop disposes it and reconnects. Default true for sessions that don't
    /// track health.
    /// </summary>
    bool IsAlive => true;

    /// <summary>
    /// Builds this device's on-wire datagram from the shared 12-byte RTP header
    /// and the raw PCM payload. RAOP wraps the PCM as AES-encrypted ALAC;
    /// AirPlay 2 sends ChaCha20-encrypted PCM — same header and timing for both.
    /// </summary>
    byte[] PrepareWirePacket(byte[] rtpHeader, byte[] pcm, ushort sequenceNumber, uint rtpTimestamp);

    void NoteRtpTime(uint rtpTime);

    /// <summary>Last volume observed on the device (set by us or fetched), linear 0..1; null until known.</summary>
    float? LastKnownVolume => null;

    /// <summary>
    /// Pulls the device's current volume via RTSP GET_PARAMETER (linear 0..1).
    /// Read-only: never changes what the device is set to. Null when the
    /// device doesn't answer (not every receiver implements it).
    /// </summary>
    Task<float?> GetVolumeAsync(CancellationToken ct) => Task.FromResult<float?>(null);

    /// <summary>Live transport/timeline snapshot for the web UI.</summary>
    SessionTelemetry GetTelemetry();

    Task SetVolumeAsync(float volume, CancellationToken ct);
    Task SetMetadataAsync(TrackMetadata metadata, CancellationToken ct);
    Task FlushAsync(ushort nextSeq, uint nextRtpTime, CancellationToken ct);
    Task TeardownAsync(CancellationToken ct);
}
