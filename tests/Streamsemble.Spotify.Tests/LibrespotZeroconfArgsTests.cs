using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Streamsemble.Spotify.Tests;

public class LibrespotZeroconfArgsTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(57500, true)]
    public async Task ZeroconfPortSelectsTheExternalBackend(int zeroconfPort, bool expectExternal)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("streamsemble-librespot-").FullName;
        try
        {
            var argsFile = Path.Combine(dir, "args");
            var fake = Path.Combine(dir, "librespot");
            File.WriteAllText(fake, $$"""
                #!/bin/sh
                printf '%s\n' "$@" > "{{argsFile}}.tmp"
                mv "{{argsFile}}.tmp" "{{argsFile}}"
                exec sleep 60
                """.Replace("\r\n", "\n"));
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var source = new LibrespotSource(
                new SpotifyOptions
                {
                    LibrespotPath = fake,
                    CacheDirectory = Path.Combine(dir, "cache"),
                    ZeroconfPort = zeroconfPort,
                },
                "Test",
                NullLogger<LibrespotSource>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = source.RunAsync(cts.Token);
            while (!File.Exists(argsFile) && !cts.IsCancellationRequested)
            {
                await Task.Delay(50);
            }

            cts.Cancel();
            await run;

            var args = string.Join(' ', File.ReadAllLines(argsFile));
            Assert.Equal(expectExternal, args.Contains("--zeroconf-port 57500 --zeroconf-backend external"));
            Assert.Equal(expectExternal, args.Contains("--zeroconf-backend"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
