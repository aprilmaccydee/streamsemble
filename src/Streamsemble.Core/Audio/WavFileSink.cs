using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Streamsemble.Core.Abstractions;
using Streamsemble.Core.Metadata;

namespace Streamsemble.Core.Audio;

/// <summary>
/// Writes each stream to a timestamped .wav file in the configured directory.
/// M0 verification sink: play Spotify at the hub, inspect the file.
/// </summary>
public sealed class WavFileSink(string directory, ILogger<WavFileSink> logger) : IAudioSink
{
    private FileStream? _file;
    private AudioFormat _format;
    private int _streamCounter;

    public Task StartStreamAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"stream-{DateTime.Now:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _streamCounter)}.wav");
        _file = new FileStream(path, FileMode.Create, FileAccess.Write);
        _format = format;
        WriteHeader(_file, format, dataLength: 0);
        logger.LogInformation("Recording stream to {Path}", path);
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(PcmFrame frame, CancellationToken cancellationToken = default)
    {
        if (_file is { } file)
        {
            await file.WriteAsync(frame.Data, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Volume → {Volume:P0}", volume);
        return Task.CompletedTask;
    }

    public Task SetMetadataAsync(TrackMetadata metadata, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Now playing: {Artist} — {Title}", metadata.Artist, metadata.Title);
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task StopStreamAsync(CancellationToken cancellationToken = default)
    {
        if (_file is not { } file)
        {
            return;
        }

        _file = null;
        var dataLength = (int)(file.Length - 44);
        file.Position = 0;
        WriteHeader(file, _format, dataLength);
        await file.DisposeAsync().ConfigureAwait(false);
        logger.LogInformation("Closed recording ({Seconds:F1} s)", (double)dataLength / _format.SampleRate / _format.BlockAlign);
    }

    private static void WriteHeader(Stream stream, AudioFormat format, int dataLength)
    {
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataLength);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], format.SampleRate * format.BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)format.BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], (short)format.BitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataLength);
        stream.Write(header);
    }
}
