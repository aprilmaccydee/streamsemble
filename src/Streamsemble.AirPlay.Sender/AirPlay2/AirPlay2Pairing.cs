using System.Net;
using Microsoft.Extensions.Logging;
using Streamsemble.AirPlay.Common.Hap;
using Streamsemble.AirPlay.Sender.Raop;

namespace Streamsemble.AirPlay.Sender.AirPlay2;

/// <summary>The secret a completed pairing produced, and which route produced it.</summary>
public sealed record AirPlay2PairingResult(byte[] SharedSecret, string Mode);

/// <summary>
/// The HAP pairing dance an AirPlay 2 receiver expects before it will accept an
/// encrypted RTSP channel: pair-verify against a stored identity if we have one
/// with this device, otherwise transient pair-setup.
///
/// Receivers route pairing by the <c>X-Apple-HKP</c> header — without it they
/// answer 403 — and the two routes use different values, which is the only
/// subtlety here.
/// </summary>
public static class AirPlay2Pairing
{
    /// <summary>Transient (HomePods, Sonos): no persistent identity, no PIN.</summary>
    private const int TransientHkp = 4;

    /// <summary>
    /// Verified (TVs): pair-setup then pair-verify against a stored identity.
    /// 3 is what a real controller sends; 6 is a different handler that rejects
    /// our SRP proof. Overridable for probing an unfamiliar receiver.
    /// </summary>
    private static int VerifiedHkp =>
        int.TryParse(Environment.GetEnvironmentVariable("STREAMSEMBLE_HKP"), out var value) ? value : 3;

    private static Dictionary<string, string> Headers(int hkp) => new()
    {
        ["X-Apple-HKP"] = hkp.ToString(),
        ["Connection"] = "keep-alive",
    };

    public static async Task<AirPlay2PairingResult> PairAsync(
        RtspClient rtsp,
        IPAddress address,
        string displayName,
        ILogger logger,
        CancellationToken ct)
    {
        var store = HomeKitPairingStore.Load();
        if (store.GetAccessory(address.ToString()) is { } accessory)
        {
            var verify = new PairVerifyClient(store, accessory);
            var m2 = await PostAsync(rtsp, "/pair-verify", verify.BuildM1(), VerifiedHkp, displayName, ct).ConfigureAwait(false);
            var m3 = verify.HandleM2BuildM3(m2);
            verify.HandleM4(await PostAsync(rtsp, "/pair-verify", m3, VerifiedHkp, displayName, ct).ConfigureAwait(false));
            logger.LogInformation("{Name}: paired (verified, stored identity)", displayName);
            return new AirPlay2PairingResult(verify.SharedSecret, "verified");
        }

        var client = new TransientPairSetupClient();

        // Some receivers require this before pair-setup; its response is not
        // used, so a failure here is not a reason to stop.
        try
        {
            await rtsp.RequestAsync("POST", ct, "application/octet-stream", [], Headers(TransientHkp), "/pair-pin-start")
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "{Name}: /pair-pin-start failed (continuing)", displayName);
        }

        var setupM2 = await PostAsync(rtsp, "/pair-setup", client.BuildM1(), TransientHkp, displayName, ct).ConfigureAwait(false);
        var setupM3 = client.HandleM2BuildM3(setupM2);
        client.HandleM4(await PostAsync(rtsp, "/pair-setup", setupM3, TransientHkp, displayName, ct).ConfigureAwait(false));

        var secret = client.SharedSecret
            ?? throw new InvalidOperationException($"{displayName}: transient pairing produced no shared secret");
        logger.LogInformation("{Name}: paired (transient)", displayName);
        return new AirPlay2PairingResult(secret, "transient");
    }

    private static async Task<byte[]> PostAsync(
        RtspClient rtsp, string path, byte[] body, int hkp, string displayName, CancellationToken ct)
    {
        var response = await rtsp.RequestAsync("POST", ct, "application/octet-stream", body, Headers(hkp), path)
            .ConfigureAwait(false);
        return response.IsSuccess
            ? response.Body
            : throw new IOException($"{displayName}: {path} returned {response.StatusCode} {response.ReasonPhrase}");
    }
}
