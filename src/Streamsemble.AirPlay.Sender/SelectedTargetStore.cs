namespace Streamsemble.AirPlay.Sender;

/// <summary>
/// The user's current choice of output speakers, mutable at runtime from the
/// web UI. <see cref="AirPlayTargetGroup"/> subscribes to <see cref="Changed"/>
/// and reconciles its live sessions to match. Seeded from configuration at
/// startup so appsettings targets still work headlessly.
/// </summary>
public sealed class SelectedTargetStore
{
    private readonly object _gate = new();
    private List<AirPlayTargetOptions> _selected = [];

    public event EventHandler? Changed;

    public IReadOnlyList<AirPlayTargetOptions> Current
    {
        get
        {
            lock (_gate)
            {
                return _selected.ToList();
            }
        }
    }

    public void Set(IEnumerable<AirPlayTargetOptions> targets)
    {
        lock (_gate)
        {
            _selected = targets.ToList();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
