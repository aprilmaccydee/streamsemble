using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Audio;

namespace Streamsemble.Cast.Stub;

public interface ICastSource : IAudioSource;

/// <summary>
/// Placeholder Google Cast source. A functional Cast receiver is not
/// implementable for official sender apps: CastV2 DeviceAuth requires a
/// device certificate chain signed by Google's CA, which senders (Chrome,
/// the Spotify/YouTube apps, …) verify before session setup — an emulated
/// receiver is discovered but always rejected. This type keeps the source
/// slot wired into the arbiter so a future licensed path drops in unchanged.
/// </summary>
public sealed class CastStubSource() : AudioSourceBase("Cast"), ICastSource
{
    public override Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
