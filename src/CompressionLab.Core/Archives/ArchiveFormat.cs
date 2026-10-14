using System.Text.Json;

namespace CompressionLab.Core.Archives;

/// <summary>Binary layout and bounds for CLCS0001. No compression or benchmark logic.</summary>
internal static class ArchiveFormat
{
    private static readonly byte[] Magic = "CLCS0001"u8.ToArray();
    private const int MaxHeaderBytes = 65536;
    private const long MaxOriginalBytes = 64L * 1024 * 1024 * 1024;
    private const int EndOfArchive = -1;

    public static void WriteHeader(BinaryWriter writer, ArchiveHeader header)
    {
        var metadata = JsonSerializer.SerializeToUtf8Bytes(header, ArchiveEngine.Json);
        if (metadata.Length > MaxHeaderBytes)
        {
            throw new InvalidDataException("Заголовок архива слишком большой");
        }

        writer.Write(Magic);
        writer.Write(metadata.Length);
        writer.Write(metadata);
    }

    public static ArchiveHeader ReadHeader(BinaryReader reader)
    {
        if (!ReadExact(reader, Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Ожидается архив .clcs");
        }

        var headerLength = reader.ReadInt32();
        if (headerLength is < 1 or > MaxHeaderBytes)
        {
            throw new InvalidDataException("Некорректный заголовок");
        }

        var header = JsonSerializer.Deserialize<ArchiveHeader>(ReadExact(reader, headerLength), ArchiveEngine.Json)
            ?? throw new InvalidDataException("Пустой заголовок");
        ValidateHeader(header);
        return header;
    }

    public static void WriteBlock(BinaryWriter writer, EncodedBlock block)
    {
        foreach (var length in block.StageLengths)
        {
            writer.Write(length);
        }

        writer.Write(block.Payload);
    }

    public static EncodedBlock? ReadBlock(BinaryReader reader, ArchiveHeader header, long restoredBytes)
    {
        var originalLength = reader.ReadInt32();
        if (originalLength == EndOfArchive)
        {
            return null;
        }

        if (originalLength < 1 || originalLength > header.BlockSize || restoredBytes + originalLength > header.OriginalSize)
        {
            throw new InvalidDataException("Неверный размер блока");
        }

        var lengths = new int[header.Steps.Length + 1];
        lengths[0] = originalLength;

        for (var index = 1; index < lengths.Length; index++)
        {
            lengths[index] = reader.ReadInt32();
            if (lengths[index] is < 0 or > ArchiveEngine.MaxIntermediate)
            {
                throw new InvalidDataException("Неверный размер промежуточного блока");
            }
        }

        return new(ReadExact(reader, lengths[^1]), lengths);
    }

    public static void WriteEnd(BinaryWriter writer) => writer.Write(EndOfArchive);

    private static void ValidateHeader(ArchiveHeader header)
    {
        if (header.Format != 1 || header.OriginalSize is < 0 or > MaxOriginalBytes ||
            header.BlockSize is < BenchmarkSettings.MinBlockBytes or > BenchmarkSettings.MaxBlockBytes ||
            header.Steps is null || header.Steps.Length is < 1 or > BenchmarkSettings.MaxSteps ||
            string.IsNullOrWhiteSpace(header.Name) || header.Sha256 is null || header.Sha256.Length != 64)
        {
            throw new InvalidDataException("Некорректные параметры архива");
        }

        foreach (var step in header.Steps)
        {
            if (step is null || string.IsNullOrWhiteSpace(step.Id) || step.Options is null || string.IsNullOrWhiteSpace(step.Version))
            {
                throw new InvalidDataException("Некорректное описание кодека");
            }
        }
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new EndOfStreamException("Архив обрезан");
        }

        return bytes;
    }
}

internal sealed record EncodedBlock(byte[] Payload, int[] StageLengths);
