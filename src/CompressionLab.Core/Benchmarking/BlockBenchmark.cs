using System.Diagnostics;
using CompressionLab.Core.Archives;

namespace CompressionLab.Core.Benchmarking;

/// <summary>Accumulates per-repeat codec timings across blocks, excluding verification and disk I/O.</summary>
internal sealed class BlockBenchmark(CodecPipeline pipeline, int repeats, int warmup)
{
    public double[] EncodeSamplesMs { get; } = new double[repeats];
    public double[] DecodeSamplesMs { get; } = new double[repeats];
    public double MedianEncodeMs => Median(EncodeSamplesMs);
    public double MedianDecodeMs => Median(DecodeSamplesMs);

    public VerifiedBlock Run(byte[] original, CancellationToken cancellationToken)
    {
        EncodedBlock? encoded = null;
        byte[] restored = [];

        for (var run = -warmup; run < repeats; run++)
        {
            var watch = Stopwatch.StartNew();
            encoded = pipeline.Encode(original, cancellationToken);
            watch.Stop();
            if (run >= 0)
            {
                EncodeSamplesMs[run] += watch.Elapsed.TotalMilliseconds;
            }

            watch.Restart();
            restored = pipeline.Decode(encoded, cancellationToken);
            watch.Stop();
            if (run >= 0)
            {
                DecodeSamplesMs[run] += watch.Elapsed.TotalMilliseconds;
            }

            if (!original.AsSpan().SequenceEqual(restored))
            {
                throw new InvalidDataException("Кодек не восстановил исходный блок");
            }
        }

        return new(encoded!, restored);
    }

    private static double Median(double[] values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}

internal sealed record VerifiedBlock(EncodedBlock Encoded, byte[] Restored);
