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

    /// <summary>
    /// Algorithmic delay (priming) of ffmpeg's native AAC encoder, in samples:
    /// the AU emitted for input frame k decodes to input samples
    /// [k·1024 − 1024, (k+1)·1024 − 1024) — AU 0 is pure priming. Raw AAC
    /// frames on the wire carry no priming signaling (no edit list, no
    /// iTunSMPB), so a receiver decodes and plays every AU verbatim, and the
    /// sender must speak decode-output time when it maps RTP timestamps onto
    /// the capture timeline; otherwise the whole stream renders exactly this
    /// much late. The value is inherent to the encoder's single 1024-sample
    /// MDCT window, not tuning — it changes only if the encoder does.
    /// Measured against ffmpeg 8.1.2 (2026-08-08): an impulse at input sample
    /// N lands at sample N+1024 of the naive ADTS round-trip
    /// (encode with the arguments below, decode with
    /// <c>ffmpeg -f aac -i pipe:0 -f s16le -ar 44100 -ac 2 pipe:1</c>).
    /// </summary>
    public const int EncoderDelaySamples = 1024;
    internal const int QueueCapacity = 256;

    private readonly ILogger _logger;
    private readonly Process? _process;
    private readonly Stream _stdin;
    private readonly Action<Exception>? _onFailure;
    private readonly CancellationTokenSource _lifetime = new();
    private Exception? _failure;
    private int _disposed;
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

    public AacEncoderPipe(ILogger logger, Action<Exception>? onFailure = null)
    {
        _logger = logger;
        _onFailure = onFailure;
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
        _stdin = _process.StandardInput.BaseStream;
        _ = ReadFramesAsync(_process.StandardOutput.BaseStream, _lifetime.Token);
        _ = LogStderrAsync(_process.StandardError);
    }

    // Stream injection exercises the real parser, counters and failure path
    // without requiring an ffmpeg executable. The caller owns these streams.
    internal AacEncoderPipe(ILogger logger, Stream stdin, Stream stdout, Action<Exception>? onFailure = null)
    {
        _logger = logger;
        _stdin = stdin;
        _onFailure = onFailure;
        _ = ReadFramesAsync(stdout, _lifetime.Token);
    }

    public ChannelReader<byte[]> Frames => _frames.Reader;

    private long _pcmBytesIn;
    private long _framesOut;
    private long _queueOverflows;

    public long FramesProduced => Interlocked.Read(ref _framesOut);
    public int QueueDepth => _frames.Reader.Count;
    public long QueueOverflows => Interlocked.Read(ref _queueOverflows);
    public string? Failure => Volatile.Read(ref _failure)?.Message;

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
        if (Volatile.Read(ref _failure) is { } failure)
        {
            throw new IOException("AAC encoder is unavailable", failure);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            await _stdin.WriteAsync(pcm, linked.Token).ConfigureAwait(false);
            var total = Interlocked.Add(ref _pcmBytesIn, pcm.Length);
            if (total % (44100L * 4 * 10) < pcm.Length)
            {
                _logger.LogInformation("AAC pipe: {In} PCM bytes in (~{Secs:F0}s), {Out} frames out", total, total / (44100.0 * 4), FramesProduced);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Abort(ex);
            throw;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && _lifetime.IsCancellationRequested)
        {
            throw new IOException("AAC encoder stopped while accepting PCM", Volatile.Read(ref _failure) ?? ex);
        }
    }

    private async Task ReadFramesAsync(Stream stdout, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        var pending = new List<byte>(8192);
        try
        {
            while (true)
            {
                var read = await stdout.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("AAC encoder output ended unexpectedly");
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
                    if (frameLength < headerLength)
                    {
                        throw new InvalidDataException("AAC encoder emitted an invalid ADTS frame length");
                    }

                    var frame = new byte[frameLength - headerLength];
                    pending.CopyTo(offset + headerLength, frame, 0, frame.Length);
                    Interlocked.Increment(ref _framesOut);
                    if (!_frames.Writer.TryWrite(frame))
                    {
                        ct.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref _queueOverflows);
                        // Never silently omit an AU: outgoing RTP would then
                        // name the wrong content forever. Fail this session
                        // instead of blocking the shared PCM fan-out.
                        throw new IOException($"AAC output queue exceeded {QueueCapacity} frames");
                    }

                    DumpFrame(frame);
                    offset += frameLength;
                }

                pending.RemoveRange(0, offset);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Abort(ex);
        }
        finally
        {
            _frames.Writer.TryComplete(Volatile.Read(ref _failure));
        }
    }

    /// <summary>Fail once and release any pending input/output IO.</summary>
    internal void Abort(Exception failure)
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.CompareExchange(ref _failure, failure, null) is not null)
        {
            return;
        }

        _frames.Writer.TryComplete(failure);
        _lifetime.Cancel();
        try
        {
            // A fatal pipeline can never resume. Terminate ffmpeg now so
            // even a blocked OS stdin pipe releases before group reconnect.
            _process?.Kill();
        }
        catch
        {
            // It may already have exited or been disposed by reconciliation.
        }

        _onFailure?.Invoke(failure);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        try
        {
            if (_process is { } process)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(500))
                {
                    process.Kill();
                }
            }
        }
        catch
        {
            // Best effort; the process may already be gone.
        }

        _process?.Dispose();
        // Pending reader/writer continuations still use this cancellation
        // source. Leave it for GC rather than racing their cancellation path.
    }
}
