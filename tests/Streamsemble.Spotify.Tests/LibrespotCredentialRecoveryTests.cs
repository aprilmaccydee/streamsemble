using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Streamsemble.Spotify.Tests;

public class LibrespotCredentialRecoveryTests
{
    /// <summary>
    /// Drives the real supervisor against a stand-in librespot that behaves
    /// like the real one with a revoked login: it dies at startup with
    /// INVALID_CREDENTIALS for as long as credentials.json exists, and stays
    /// up (discovery mode) once it is gone.
    /// </summary>
    [Fact]
    public async Task RejectedCredentialsAreMovedAsideSoTheNextLaunchStaysUp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("streamsemble-librespot-").FullName;
        try
        {
            var cache = Path.Combine(dir, "cache");
            Directory.CreateDirectory(cache);
            var credentials = Path.Combine(cache, "credentials.json");
            File.WriteAllText(credentials, "{}");
            var launches = Path.Combine(dir, "launches");
            var healthy = Path.Combine(dir, "healthy");

            var fake = Path.Combine(dir, "librespot");
            File.WriteAllText(fake, $$"""
                #!/bin/sh
                echo launch >> "{{launches}}"
                if [ -f "{{credentials}}" ]; then
                  echo "[2026-11-16T12:57:50Z ERROR librespot] could not initialize spirc: Invalid state { Login request was denied: INVALID_CREDENTIALS }" >&2
                  exit 1
                fi
                touch "{{healthy}}"
                exec sleep 60
                """.Replace("\r\n", "\n"));
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var source = new LibrespotSource(
                new SpotifyOptions { LibrespotPath = fake, CacheDirectory = cache },
                "Test",
                NullLogger<LibrespotSource>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = source.RunAsync(cts.Token);
            while (!File.Exists(healthy) && !cts.IsCancellationRequested)
            {
                await Task.Delay(50);
            }

            cts.Cancel();
            await run;

            Assert.True(File.Exists(healthy), "librespot never came up without the rejected credentials");
            Assert.False(File.Exists(credentials));
            Assert.True(File.Exists(credentials + ".rejected"));
            Assert.Equal(2, File.ReadAllLines(launches).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
