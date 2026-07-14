using Streamsemble.Core.Audio;

namespace Streamsemble.Core.Abstractions;

/// <summary>
/// Decodes one compressed audio packet (ALAC or AAC-LC from an AirPlay
/// stream) to canonical-format PCM. Implementations are chosen per stream
/// from the SETUP descriptor.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    AudioFormat OutputFormat { get; }

    /// <summary>Returns the number of bytes written to <paramref name="pcmOut"/>.</summary>
    int DecodePacket(ReadOnlySpan<byte> packet, Span<byte> pcmOut);
}
