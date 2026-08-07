using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Streamsemble.Wled;

/// <summary>
/// One WLED device driven over its UDP realtime protocol. While packets keep
/// arriving the strip shows exactly what it is sent; once they stop it returns
/// to its own effect after the configured timeout. Light mode, color and
/// brightness are runtime-tunable (web UI); the wire protocol and strip
/// geometry are configuration.
/// </summary>
public sealed class WledDevice : IDisposable
{
    private readonly WledDeviceOptions _options;
    private readonly ILogger _logger;
    private readonly UdpClient _udp = new();
    private IPEndPoint? _endpoint;
    private int _colorPacked;

    public WledDevice(WledDeviceOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new InvalidOperationException("A WLED device entry needs a Host");
        }

        _options = options;
        _logger = logger;
        Protocol = WledRealtimeModes.Parse(options.Protocol);
        Mode = WledLightModes.Parse(options.Mode);
        Color = ParseColor(options.Color);
        Brightness = (float)Math.Clamp(options.Brightness, 0.0, 1.0);
        Palette = WledPalettes.Parse(options.Palette);
        Decay = (float)Math.Clamp(options.Decay, 0.0, 1.0);
        Mirror = options.Mirror;
        Reverse = options.Reverse;
    }

    public string Name => _options.Name ?? _options.Host;

    public int LedCount => _options.LedCount;

    public WledRealtimeMode Protocol { get; }

    /// <summary>How this strip visualizes the music; Off leaves it to manual sends.</summary>
    public WledLightMode Mode { get; set; }

    /// <summary>Master brightness scale for rendered light frames, 0–1.</summary>
    public float Brightness { get; set; }

    /// <summary>Color treatment for the light modes.</summary>
    public WledPalette Palette { get; set; }

    /// <summary>Fall half-life in seconds, 0–1.</summary>
    public float Decay { get; set; }

    /// <summary>Render from the strip's center outward.</summary>
    public bool Mirror { get; set; }

    /// <summary>Flip the strip end-for-end.</summary>
    public bool Reverse { get; set; }

    /// <summary>This strip's renderer — per-device visual state (fall
    /// envelopes, rainbow phase), touched only by the lighting send loop.</summary>
    public WledLightRenderer Renderer { get; } = new();

    /// <summary>The render tunables, snapshotted for one light frame.</summary>
    public WledRenderSettings RenderSettings => new(Color, Palette, Decay, Mirror, Reverse);

    /// <summary>Base color for the Pulse mode. Packed so cross-thread updates can't tear.</summary>
    public (byte R, byte G, byte B) Color
    {
        get
        {
            var packed = Volatile.Read(ref _colorPacked);
            return ((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        }
        set => Volatile.Write(ref _colorPacked, (value.R << 16) | (value.G << 8) | value.B);
    }

    public string ColorHex
    {
        get
        {
            var (r, g, b) = Color;
            return $"#{r:X2}{g:X2}{b:X2}";
        }
    }

    public static (byte R, byte G, byte B) ParseColor(string color)
    {
        var hex = color.TrimStart('#');
        return hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var packed)
            ? ((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : throw new InvalidOperationException($"Unparseable WLED color \"{color}\" (expected #RRGGBB)");
    }

    /// <summary>Fills the whole strip with one color.</summary>
    public Task SendColorAsync(byte r, byte g, byte b, CancellationToken cancellationToken = default)
    {
        var rgb = new byte[_options.LedCount * 3];
        for (var i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = r;
            rgb[i + 1] = g;
            rgb[i + 2] = b;
        }

        return SendPixelsAsync(rgb, 0, cancellationToken);
    }

    /// <summary>Sends per-LED RGB triples (3 bytes per pixel) starting at <paramref name="startLed"/>.</summary>
    public async Task SendPixelsAsync(ReadOnlyMemory<byte> rgb, int startLed = 0, CancellationToken cancellationToken = default)
    {
        var timeout = (byte)Math.Clamp(_options.TimeoutSeconds, 1, 255);
        foreach (var packet in WledPacketBuilder.BuildPackets(Protocol, timeout, startLed, rgb))
        {
            await SendAsync(packet, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Hands the device back to its own effect about a second from now,
    /// even when the configured timeout would hold realtime mode much longer.</summary>
    public Task ReleaseAsync(CancellationToken cancellationToken = default)
        => SendAsync([(byte)Protocol, 1], cancellationToken);

    private async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = _endpoint ?? await ResolveAsync(cancellationToken).ConfigureAwait(false);
            await _udp.SendAsync(packet, endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            // Drop the cached address so a device that moved (DHCP renew,
            // mDNS rename) gets re-resolved on the next attempt.
            _endpoint = null;
            _logger.LogWarning("WLED send to {Name} ({Host}:{Port}) failed: {Error}",
                Name, _options.Host, _options.Port, ex.Message);
            throw;
        }
    }

    private async Task<IPEndPoint> ResolveAsync(CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(_options.Host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new SocketException((int)SocketError.HostNotFound);
        _logger.LogInformation("WLED device {Name} resolved to {Address}:{Port}", Name, address, _options.Port);
        return _endpoint = new IPEndPoint(address, _options.Port);
    }

    public void Dispose() => _udp.Dispose();
}
