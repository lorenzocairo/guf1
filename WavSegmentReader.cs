using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Guf1;

public static class WavSegmentReader
{
    private const ushort PcmFormat = 1;
    private const ushort FloatFormat = 3;
    private const ushort ExtensibleFormat = 0xFFFE;

    public static WavSegment ReadMonoSegment(string filePath, double startSeconds, double endSeconds)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        var header = ReadHeader(reader);
        ValidateFormat(header);

        var startFrame = Math.Max(0, (long)Math.Floor(startSeconds * header.SampleRate));
        var endFrame = Math.Max(startFrame, (long)Math.Ceiling(endSeconds * header.SampleRate));
        var availableFrames = header.DataSize / header.BlockAlign;
        if (startFrame >= availableFrames)
        {
            return new WavSegment(header.SampleRate, []);
        }

        endFrame = Math.Min(endFrame, availableFrames);
        var frameCount = (int)Math.Min(int.MaxValue, endFrame - startFrame);
        var samples = new float[frameCount];
        stream.Position = header.DataOffset + startFrame * header.BlockAlign;

        var frameBuffer = new byte[header.BlockAlign];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var bytesRead = reader.Read(frameBuffer, 0, frameBuffer.Length);
            if (bytesRead != frameBuffer.Length)
            {
                Array.Resize(ref samples, frame);
                break;
            }

            double monoSample = 0;
            for (var channel = 0; channel < header.Channels; channel++)
            {
                var sampleOffset = channel * header.BytesPerSample;
                monoSample += ReadSample(frameBuffer, sampleOffset, header.BitsPerSample, header.FormatTag);
            }

            samples[frame] = (float)(monoSample / header.Channels);
        }

        return new WavSegment(header.SampleRate, samples);
    }

    private static WavHeader ReadHeader(BinaryReader reader)
    {
        if (ReadFourCc(reader) != "RIFF")
        {
            throw new InvalidDataException("Il file non e un WAV RIFF valido.");
        }

        _ = reader.ReadUInt32();
        if (ReadFourCc(reader) != "WAVE")
        {
            throw new InvalidDataException("Il file non contiene un flusso WAVE valido.");
        }

        ushort? formatTag = null;
        ushort? channels = null;
        int? sampleRate = null;
        ushort? blockAlign = null;
        ushort? bitsPerSample = null;
        long? dataOffset = null;
        int? dataSize = null;

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunkId = ReadFourCc(reader);
            var chunkSize = reader.ReadUInt32();
            var chunkStart = reader.BaseStream.Position;

            if (chunkId == "fmt ")
            {
                formatTag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadInt32();
                blockAlign = reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();

                if (formatTag == ExtensibleFormat && chunkSize >= 40)
                {
                    _ = reader.ReadUInt16();
                    _ = reader.ReadUInt16();
                    _ = reader.ReadUInt32();
                    formatTag = reader.ReadUInt16();
                }
            }
            else if (chunkId == "data")
            {
                dataOffset = reader.BaseStream.Position;
                dataSize = checked((int)chunkSize);
            }

            reader.BaseStream.Position = chunkStart + chunkSize + chunkSize % 2;
        }

        if (formatTag is null || channels is null || sampleRate is null || blockAlign is null || bitsPerSample is null)
        {
            throw new InvalidDataException("Il chunk fmt del WAV non e stato trovato.");
        }

        if (dataOffset is null || dataSize is null)
        {
            throw new InvalidDataException("Il chunk data del WAV non e stato trovato.");
        }

        return new WavHeader(
            formatTag.Value,
            channels.Value,
            sampleRate.Value,
            blockAlign.Value,
            bitsPerSample.Value,
            dataOffset.Value,
            dataSize.Value);
    }

    private static void ValidateFormat(WavHeader header)
    {
        if (header.Channels <= 0)
        {
            throw new InvalidDataException("Il WAV non contiene canali audio validi.");
        }

        if (header.SampleRate <= 0)
        {
            throw new InvalidDataException("Il WAV non contiene un sample rate valido.");
        }

        if (header.FormatTag == PcmFormat && header.BitsPerSample is not (8 or 16 or 24 or 32))
        {
            throw new NotSupportedException($"WAV PCM a {header.BitsPerSample} bit non supportato.");
        }

        if (header.FormatTag == FloatFormat && header.BitsPerSample != 32)
        {
            throw new NotSupportedException($"WAV float a {header.BitsPerSample} bit non supportato.");
        }

        if (header.FormatTag is not (PcmFormat or FloatFormat))
        {
            throw new NotSupportedException("Formato WAV non supportato.");
        }
    }

    private static double ReadSample(byte[] buffer, int offset, ushort bitsPerSample, ushort formatTag)
    {
        if (formatTag == FloatFormat)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        return bitsPerSample switch
        {
            8 => (buffer[offset] - 128) / 128.0,
            16 => BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2)) / 32768.0,
            24 => ReadInt24LittleEndian(buffer, offset) / 8388608.0,
            32 => BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4)) / 2147483648.0,
            _ => 0
        };
    }

    private static int ReadInt24LittleEndian(byte[] buffer, int offset)
    {
        var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
        {
            throw new EndOfStreamException("Header WAV incompleto.");
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private sealed record WavHeader(
        ushort FormatTag,
        ushort Channels,
        int SampleRate,
        ushort BlockAlign,
        ushort BitsPerSample,
        long DataOffset,
        int DataSize)
    {
        public int BytesPerSample => BitsPerSample / 8;
    }
}

public sealed class WavSegment
{
    public WavSegment(int sampleRate, float[] samples)
    {
        SampleRate = sampleRate;
        Samples = samples;
    }

    public int SampleRate { get; }
    public float[] Samples { get; }
}
