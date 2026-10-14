namespace CompressionLab.Core;

public sealed record CodecStep(string Id, Dictionary<string, int> Options, string? Version = null);
public sealed record ArchiveHeader(int Format, string Name, long OriginalSize, string Sha256, int BlockSize, CodecStep[] Steps);
public sealed record BenchmarkResult(long OriginalBytes, long ArchiveBytes, long PayloadBytes, double? SavedPercent,
    double EncodeMs, double DecodeMs, double[] EncodeSamplesMs, double[] DecodeSamplesMs, string Sha256, bool Verified);
