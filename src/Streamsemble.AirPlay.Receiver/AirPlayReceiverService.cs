using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Streamsemble.Discovery;

namespace Streamsemble.AirPlay.Receiver;

/// <summary>
/// Publishes the AirPlay receiver's mDNS records so the hub appears as a
/// speaker, and (M3) will host the RTSP server that drives pairing and audio
/// receipt. Disabled by default until the M3 protocol path is implemented —
/// advertising a device that cannot complete pairing would make iOS show a
/// speaker that fails to connect.
/// </summary>
public sealed class AirPlayReceiverService(
    IOptions<AirPlayReceiverOptions> options,
    ServiceAdvertiser advertiser,
    ILogger<AirPlayReceiverService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("AirPlay receiver disabled (scaffolding only; enable once M3 pairing/audio lands)");
            return Task.CompletedTask;
        }

        // Feature flags for a buffered-audio, transient-pairing receiver. The
        // real bitmask is copied from goplay2/shairport-sync in M3; this makes
        // the device visible for protocol bring-up with Wireshark.
        var name = options.Value.Name ?? "Streamsemble";
        var deviceId = "9F:D7:AF:12:34:56";
        var txt = new Dictionary<string, string>
        {
            ["deviceid"] = deviceId,
            ["features"] = "0x445F8A00,0x1C340",
            ["srcvers"] = "366.0",
            ["flags"] = "0x4",
            ["model"] = "Streamsemble1,1",
            ["pk"] = "",
        };

        advertiser.Advertise("_airplay._tcp", name, options.Value.Port, txt);
        advertiser.Advertise("_raop._tcp", $"{deviceId.Replace(":", "")}@{name}", options.Value.Port, new Dictionary<string, string>
        {
            ["cn"] = "0,1",
            ["et"] = "0,1",
            ["tp"] = "UDP",
            ["vs"] = "366.0",
            ["am"] = "Streamsemble1,1",
        });
        advertiser.Start();

        logger.LogWarning("AirPlay receiver advertising only — pairing/audio not implemented yet (M3)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
