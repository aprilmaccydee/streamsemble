using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Streamsemble.AirPlay.Sender.AirPlay2;

/// <summary>
/// Streams canonical PCM (s16le 44100 stereo) through an ffmpeg subprocess and
/// yields raw AAC-LC frames (1024 samples each) for the buffered AirPlay 2
/// stream. ffmpeg emits ADTS; the 7/9-byte ADTS header is stripped here since
/// the RTP payload carries raw AAC frames.
/// </summary>
public sealed class AacEncoderPipe : IDisposable
{
    public const int SamplesPerFrame = 1024;

    private readonly ILogger _logger;
    private readonly Process _process;
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

    public AacEncoderPipe(ILogger logger)
    {
        _logger = logger;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-hide_banner -loglevel error -f s16le -ar 44100 -ac 2 -i pipe:0 " +
                            "-c:a aac -b:a 192k -ar 44100 -f adts pipe:1",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        _process.Start();
        _ = ReadFramesAsync(_process.StandardOutput.BaseStream);
        _ = LogStderrAsync(_process.StandardError);
    }

    public ChannelReader<byte[]> Frames => _frames.Reader;

    private long _pcmBytesIn;
    private long _framesOut;

    /// <summary>
    /// Total PCM samples fed into the encoder so far. Compared against the
    /// sample index of the frame being sent, this measures the live pipeline
    /// delay (ffmpeg stdin buffering + encoder queue) so the buffered anchor
    /// can compensate for it dynamically.
    /// </summary>
    public long PcmSamplesIn => Interlocked.Read(ref _pcmBytesIn) / 4;

    /// <summary>Feeds interleaved s16le stereo PCM; ffmpeg paces itself off the pipe.</summary>
    public async ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        await _process.StandardInput.BaseStream.WriteAsync(pcm, ct).ConfigureAwait(false);
        var total = Interlocked.Add(ref _pcmBytesIn, pcm.Length);
        if (total % (44100L * 4 * 10) < pcm.Length)
        {
            _logger.LogInformation("AAC pipe: {In} PCM bytes in (~{Secs:F0}s), {Out} frames out", total, total / (44100.0 * 4), _framesOut);
        }
    }

    private async Task ReadFramesAsync(Stream stdout)
    {
        var buffer = new byte[32 * 1024];
        var pending = new List<byte>(8192);
        try
        {
            while (true)
            {
                var read = await stdout.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                pending.AddRange(buffer.AsSpan(0, read).ToArray());

                // Walk complete ADTS frames: 12-bit sync 0xFFF, frame length at
                // bits 30..43 (includes the header), protection_absent decides
                // whether the header is 7 or 9 bytes.
                var offset = 0;
                while (pending.Count - offset >= 7)
                {
                    if (pending[offset] != 0xFF || (pending[offset + 1] & 0xF0) != 0xF0)
                    {
                        offset++; // resync (shouldn't happen on a clean pipe)
                        continue;
                    }

                    var frameLength = (pending[offset + 3] & 0x03) << 11
                                      | pending[offset + 4] << 3
                                      | pending[offset + 5] >> 5;
                    if (pending.Count - offset < frameLength)
                    {
                        break; // incomplete frame; wait for more bytes
                    }

                    var headerLength = (pending[offset + 1] & 0x01) != 0 ? 7 : 9;
                    var frame = new byte[frameLength - headerLength];
                    pending.CopyTo(offset + headerLength, frame, 0, frame.Length);
                    _frames.Writer.TryWrite(frame);
                    _framesOut++;
                    DumpFrame(frame);
                    offset += frameLength;
                }

                pending.RemoveRange(0, offset);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    // Diagnostic tap: STREAMSEMBLE_AAC_DUMP=<path> writes every raw frame as
    // [u16le length][frame] so the exact wire payloads can be re-wrapped in
    // ADTS offline and decoded to verify integrity.
    private FileStream? _dump;
    private bool _dumpChecked;

    private void DumpFrame(byte[] frame)
    {
        if (!_dumpChecked)
        {
            _dumpChecked = true;
            if (Environment.GetEnvironmentVariable("STREAMSEMBLE_AAC_DUMP") is { Length: > 0 } path)
            {
                _dump = File.Create(path);
            }
        }

        if (_dump is { } dump)
        {
            Span<byte> len = stackalloc byte[2];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)frame.Length);
            dump.Write(len);
            dump.Write(frame);
            dump.Flush();
        }
    }

    private async Task LogStderrAsync(StreamReader stderr)
    {
        try
        {
            while (await stderr.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _logger.LogWarning("ffmpeg: {Line}", line);
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
