using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompressionLab.Core.Archives;
using CompressionLab.Core.Benchmarking;

namespace CompressionLab.Core;

/// <summary>Coordinates file I/O, the container format and independently testable codec execution.</summary>
public sealed class ArchiveEngine(CodecRegistry registry)
{
    public const int MaxBlock = BenchmarkSettings.MaxBlockBytes;
    public const int MaxIntermediate = 32 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public BenchmarkResult Compress(
        string source, string destination, string name, CodecStep[] steps,
        int blockSize, int repeats, int warmup, CancellationToken cancellationToken)
    {
        BenchmarkSettings.Validate(blockSize, repeats, warmup);
        steps = registry.NormalizeSteps(steps);
        cancellationToken.ThrowIfCancellationRequested();

        using var input = File.OpenRead(source);
        var hash = Convert.ToHexString(SHA256.HashData(input));
        input.Position = 0;
        var header = new ArchiveHeader(1, Path.GetFileName(name), input.Length, hash, blockSize, steps);
        var benchmark = new BlockBenchmark(new CodecPipeline(registry, steps), repeats, warmup);
        var created = false;
        var complete = false;
        long payloadBytes = 0;

        try
        {
            using (var output = new FileStream(destination, FileMode.CreateNew))
            using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            using (var restoredHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                created = true;
                ArchiveFormat.WriteHeader(writer, header);
                var buffer = new byte[blockSize];
                int read;

                while ((read = input.ReadAtLeast(buffer, blockSize, throwOnEndOfStream: false)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var block = benchmark.Run(buffer.AsSpan(0, read).ToArray(), cancellationToken);
                    ArchiveFormat.WriteBlock(writer, block.Encoded);
                    payloadBytes += block.Encoded.Payload.Length;
                    restoredHash.AppendData(block.Restored);
                }

                ArchiveFormat.WriteEnd(writer);
                VerifyHash(restoredHash, header.Sha256);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var archiveBytes = new FileInfo(destination).Length;
            complete = true;
            return new(
                input.Length, archiveBytes, payloadBytes,
                input.Length == 0 ? null : (1.0 - (double)archiveBytes / input.Length) * 100,
                benchmark.MedianEncodeMs, benchmark.MedianDecodeMs,
                benchmark.EncodeSamplesMs, benchmark.DecodeSamplesMs, hash, true);
        }
        finally
        {
            DeleteIncompleteOutput(destination, created, complete);
        }
    }

    public ArchiveHeader Restore(string archive, string destination, CancellationToken cancellationToken)
    {
        using var input = File.OpenRead(archive);
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        var header = ArchiveFormat.ReadHeader(reader);
        var steps = registry.NormalizeSteps(header.Steps);
        var pipeline = new CodecPipeline(registry, steps);
        var created = false;
        var complete = false;

        try
        {
            using (var output = new FileStream(destination, FileMode.CreateNew))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                created = true;
                long total = 0;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var block = ArchiveFormat.ReadBlock(reader, header, total);
                    if (block is null)
                    {
                        break;
                    }

                    var restored = pipeline.Decode(block, cancellationToken);
                    output.Write(restored);
                    hash.AppendData(restored);
                    total += restored.Length;
                }

                if (total != header.OriginalSize || input.Position != input.Length)
                {
                    throw new InvalidDataException("Архив повреждён: размер или лишние данные");
                }

                VerifyHash(hash, header.Sha256);
            }

            complete = true;
            return header;
        }
        finally
        {
            DeleteIncompleteOutput(destination, created, complete);
        }
    }

    private static void VerifyHash(IncrementalHash hash, string expected)
    {
        if (Convert.ToHexString(hash.GetHashAndReset()) != expected)
        {
            throw new InvalidDataException("SHA-256 не совпадает");
        }
    }

    private static void DeleteIncompleteOutput(string destination, bool created, bool complete)
    {
        // CreateNew may fail because a user file exists: only delete files created by this operation.
        if (created && !complete && File.Exists(destination))
        {
            File.Delete(destination);
        }
    }
}
