using Xunit;

namespace Streamsemble.Spotify.Tests;

public class LibrespotCredentialRejectionTests
{
    [Theory]
    [InlineData("[2026-11-16T12:57:50Z ERROR librespot] could not initialize spirc: Invalid state { Login request was denied: INVALID_CREDENTIALS }", true)]
    [InlineData("[2026-11-16T12:57:50Z ERROR librespot] Connection failed: Login failed with reason: BadCredentials", true)]
    [InlineData("[2026-11-16T12:57:50Z WARN librespot] Connection failed: Network is unreachable (os error 101)", false)]
    [InlineData("[2026-11-16T12:57:50Z INFO librespot] Using StdoutSink (pipe) with format: S16", false)]
    public void DetectsOnlyRejectedLogins(string line, bool expected) =>
        Assert.Equal(expected, LibrespotSource.IsCredentialRejection(line));
}
