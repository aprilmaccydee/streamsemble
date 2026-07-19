using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Streamsemble.AirPlay.Receiver.Audio;

/// <summary>
/// Streams raw AAC-LC frames (as received on the buffered AirPlay 2 data
/// channel) through an ffmpeg subprocess and yields canonical PCM (s16le
/// 44100 stereo). The exact mirror of the sender's <c>AacEncoderPipe</c>:
/// inbound frames are re-wrapped in the 7-byte ADTS header ffmpeg's demuxer
/// expects, and PCM comes back on stdout.
/// </summary>
public sealed class AacDecoderPipe : IDisposable
{
    public const int SamplesPerFrame = 1024;

    private readonly ILogger _logger;
    private readonly Process _process;
    private readonly Channel<byte[]> _pcm = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1024) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

    public AacDecoderPipe(ILogger logger)
    {
        _logger = logger;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-hide_banner -loglevel error -f aac -i pipe:0 " +
                            "-f s16le -ar 44100 -ac 2 pipe:1",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        _process.Start();
        _ = ReadPcmAsync(_process.StandardOutput.BaseStream);
        _ = LogStderrAsync(_process.StandardError);
    }

    /// <summary>Decoded PCM chunks (arbitrary sizes; consumers re-chunk). Bounded so a bursting sender applies backpressure here, not in memory.</summary>
    public ChannelReader<byte[]> Pcm => _pcm.Reader;

    private long _framesIn;
    private long _pcmBytesOut;

    /// <summary>Feed one raw AAC-LC frame from the wire; wrapped in ADTS for ffmpeg.</summary>
    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var adts = BuildAdtsHeader(frame.Length);
        var stdin = _process.StandardInput.BaseStream;
        await stdin.WriteAsync(adts, ct).ConfigureAwait(false);
        await stdin.WriteAsync(frame, ct).ConfigureAwait(false);
        await stdin.FlushAsync(ct).ConfigureAwait(false);

        if (++_framesIn % 2048 == 0)
        {
            _logger.LogInformation("AAC decode pipe: {In} frames in, {Out} PCM bytes out (~{Secs:F0}s)",
                _framesIn, Interlocked.Read(ref _pcmBytesOut), Interlocked.Read(ref _pcmBytesOut) / (44100.0 * 4));
        }
    }

    /// <summary>7-byte ADTS header for AAC-LC, 44.1 kHz, stereo (frame length includes the header).</summary>
    internal static byte[] BuildAdtsHeader(int rawFrameLength)
    {
        var frameLength = rawFrameLength + 7;
        return
        [
            0xFF,
            0xF1, // MPEG-4, layer 0, protection absent
            0x50, // profile AAC-LC (1), sampling index 4 = 44100, channel cfg high bit 0
            (byte)(0x80 | (frameLength >> 11)), // channel cfg 2 (stereo), length bits 12..11
            (byte)(frameLength >> 3),
            (byte)(((frameLength & 0x07) << 5) | 0x1F),
            0xFC,
        ];
    }

    private async Task ReadPcmAsync(Stream stdout)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                var read = await stdout.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                Interlocked.Add(ref _pcmBytesOut, read);
                await _pcm.Writer.WriteAsync(buffer.AsSpan(0, read).ToArray()).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _pcm.Writer.TryComplete();
        }
    }

    private async Task LogStderrAsync(StreamReader stderr)
    {
        try
        {
            while (await stderr.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _logger.LogWarning("ffmpeg(decode): {Line}", line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(500))
            {
                _process.Kill();
            }
        }
        catch
        {
            // Best effort; the process may already be gone.
        }

        _process.Dispose();
    }
}
