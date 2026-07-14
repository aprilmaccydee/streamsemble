using System.Net;
using Streamsemble.Core.Metadata;

namespace Streamsemble.AirPlay.Sender;

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
    /// Builds this device's on-wire datagram from the shared 12-byte RTP header
    /// and the raw PCM payload. RAOP wraps the PCM as AES-encrypted ALAC;
    /// AirPlay 2 sends ChaCha20-encrypted PCM — same header and timing for both.
    /// </summary>
    byte[] PrepareWirePacket(byte[] rtpHeader, byte[] pcm, ushort sequenceNumber, uint rtpTimestamp);

    void NoteRtpTime(uint rtpTime);
    Task SetVolumeAsync(float volume, CancellationToken ct);
    Task SetMetadataAsync(TrackMetadata metadata, CancellationToken ct);
    Task FlushAsync(ushort nextSeq, uint nextRtpTime, CancellationToken ct);
    Task TeardownAsync(CancellationToken ct);
}
