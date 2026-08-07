using Microsoft.Extensions.Logging;

namespace Streamsemble.Wled;

/// <summary>All configured WLED devices, addressable by name or fanned out together.</summary>
public sealed class WledDeviceGroup : IDisposable
{
    private readonly List<WledDevice> _devices;

    public WledDeviceGroup(WledOptions options, ILogger<WledDeviceGroup> logger)
        => _devices = options.Devices.Select(d => new WledDevice(d, logger)).ToList();

    public bool IsConfigured => _devices.Count > 0;

    public IReadOnlyList<WledDevice> Devices => _devices;

    /// <summary>Devices matching <paramref name="name"/> — every device when null.</summary>
    public IReadOnlyList<WledDevice> Match(string? name) => name is null
        ? _devices
        : _devices.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

    public void Dispose()
    {
        foreach (var device in _devices)
        {
            device.Dispose();
        }
    }
}
