using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Streamsemble.AirPlay.Common.Hap;
using Streamsemble.AirPlay.Receiver.Rtsp;
using Streamsemble.Discovery;
using Streamsemble.Timing.Ptp;

namespace Streamsemble.AirPlay.Receiver;

/// <summary>
/// The AirPlay 2 receiver (M3): advertises the hub as a buffered-audio,
/// transient-pairing speaker and serves the RTSP sessions that stream into
/// <see cref="AirPlayReceiverSource"/>. Disabled by default via
/// <c>AirPlayReceiver:Enabled</c>.
/// </summary>
public sealed class AirPlayReceiverService(
    IOptions<AirPlayReceiverOptions> options,
    AirPlayReceiverSource source,
    ServiceAdvertiser advertiser,
    PtpReceiverClock ptp,
    ILogger<AirPlayReceiverService> logger) : IHostedService
{
    private RtspServer? _server;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("AirPlay receiver disabled (AirPlayReceiver:Enabled=false)");
            return Task.CompletedTask;
        }

        var identity = BuildIdentity(options.Value.Name ?? "Streamsemble");

        // The process-wide hub clock (DI singleton, shared with the speaker
        // fan-out so every role runs on ONE grandmaster). Lazily binds
        // 319/320 on the first PTP session SETUP.
        _server = new RtspServer(conn => new ReceiverSession(conn, source, identity, ptp, logger), logger);
        _server.Start(options.Value.Port);

        // _airplay._tcp ONLY — deliberately no _raop._tcp. The _raop service
        // is the realtime (type 96) endpoint: every realtime-capable receiver
        // on the LAN advertises it and the buffered-only living-room TV does
        // not; advertising it made macOS pick realtime stream SETUPs (which we
        // 453) instead of falling back to buffered (rounds 11/13).
        advertiser.Advertise("_airplay._tcp", identity.Name, options.Value.Port,
            ReceiverFeatures.TxtRecords(identity));
        advertiser.Start();

        logger.LogInformation("AirPlay receiver \"{Name}\" up: RTSP :{Port}, deviceid {DeviceId}, pk {Pk}",
            identity.Name, options.Value.Port, identity.DeviceId, identity.PkHex[..16] + "…");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stable identity derived from the persisted Ed25519 keypair (the same
    /// store the sender uses for HomeKit pairing): pk is the public key that
    /// goes in the TXT records, the device id is a locally-administered MAC
    /// derived from it, and pi is a UUID over the same bytes — consistent
    /// across restarts so senders don't see a "new" speaker every launch.
    /// </summary>
    public static ReceiverIdentity BuildIdentity(string name)
    {
        var store = HomeKitPairingStore.Load();
        var pk = store.ControllerLtpk;

        var mac = (byte[])pk[..6].Clone();
        mac[0] = (byte)((mac[0] | 0x02) & 0xFE); // locally administered, unicast
        var deviceId = string.Join(":", mac.Select(b => b.ToString("X2")));

        var pi = new Guid(MD5.HashData(pk)[..16]).ToString();

        return new ReceiverIdentity(name, deviceId, pi, pk);
    }

    /// <summary>EUI-64 clock identity from the device MAC (aa:bb:cc:dd:ee:ff → aabbcc fffe ddeeff).</summary>
    public static byte[] BuildClockId(string deviceId)
    {
        var mac = deviceId.Split(':').Select(b => Convert.ToByte(b, 16)).ToArray();
        return [mac[0], mac[1], mac[2], 0xFF, 0xFE, mac[3], mac[4], mac[5]];
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.Dispose();
        return Task.CompletedTask;
    }
}
