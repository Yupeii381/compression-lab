using CompressionLab.Core;

namespace CompressionLab.Web.Models;

public sealed record LabFile(string Id, string Name, long Size, string Sha256);

public sealed record Chain(string Name, CodecStep[] Steps);

public sealed record RunRequest(
    string[] FileIds,
    Chain[] Chains,
    int BlockSize = 1048576,
    int Repeats = 3,
    int Warmup = 1);

// Keep status values stable: existing state files and the browser use these strings.
public static class JobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsActive(string status) => status is Queued or Running;
}

public sealed record Job
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string FileId { get; init; }
    public required Chain Chain { get; init; }
    public int BlockSize { get; init; }
    public int Repeats { get; init; }
    public int Warmup { get; init; }
    public string Status { get; init; } = JobStatus.Queued;
    public string? Error { get; init; }
    public BenchmarkResult? Result { get; init; }
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record LabSnapshot(LabFile[] Files, Job[] Jobs, bool Paused);
