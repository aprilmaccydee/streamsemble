using System.Globalization;
using System.Text;

namespace Streamsemble.AirPlay.Sender;

/// <summary>
/// The RTSP volume parameter wire form shared by RAOP and AirPlay 2:
/// "volume: &lt;dB&gt;" where dB runs -30 (quietest) … 0, with -144 = mute.
/// </summary>
public static class VolumeParameters
{
    /// <summary>Parses a GET_PARAMETER response body ("volume: -11.250000\r\n") to dB.</summary>
    public static double? ParseDb(byte[] body)
    {
        var text = Encoding.ASCII.GetString(body);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line[..colon].Trim().Equals("volume", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (double.TryParse(line[(colon + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
            {
                return db;
            }
        }

        return null;
    }

    public static float DbToLinear(double db)
        => db <= -100 ? 0f : Math.Clamp((float)((db + 30.0) / 30.0), 0f, 1f);
}
