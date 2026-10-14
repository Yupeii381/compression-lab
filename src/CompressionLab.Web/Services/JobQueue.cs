using CompressionLab.Core;
using CompressionLab.Web.Models;
using CompressionLab.Web.Storage;

namespace CompressionLab.Web.Services;

/// <summary>Owns the running job's cancellation; persistence has no dependency on worker lifetime.</summary>
public sealed class JobQueue(LabRepository repository, ExperimentValidator validator)
{
    private readonly object gate = new();
    private RunningJob? active;
    private bool updating;

    public void Enqueue(RunRequest request)
    {
        lock (gate)
        {
            if (updating)
            {
                throw new ArgumentException("Дождитесь завершения обновления приложения");
            }

            repository.Enqueue(validator.CreateJobs(request));
        }
    }

    public void BeginUpdate()
    {
        lock (gate)
        {
            if (active is not null)
            {
                throw new ArgumentException("Дождитесь завершения текущего эксперимента или отмените его перед обновлением");
            }

            updating = true;
        }
    }

    public void EndUpdate()
    {
        lock (gate) updating = false;
    }
    public void TogglePause() => repository.TogglePause();

    public RunningJob? TryStartNext(CancellationToken stoppingToken)
    {
        lock (gate)
        {
            if (active is not null || updating)
            {
                return null;
            }

            var job = repository.TryStartNext();
            if (job is null)
            {
                return null;
            }

            active = new(job, CancellationTokenSource.CreateLinkedTokenSource(stoppingToken));
            return active;
        }
    }

    public void Cancel(string id)
    {
        lock (gate)
        {
            repository.UpdateJob(id, job => job.Status == JobStatus.Queued
                ? job with { Status = JobStatus.Cancelled }
                : job);

            if (active?.Job.Id == id)
            {
                active.CancelledByUser = true;
                active.Cancellation.Cancel();
            }
        }
    }

    public void Retry(string id)
    {
        repository.UpdateJob(id, job => job.Status is JobStatus.Failed or JobStatus.Cancelled
            ? job with { Status = JobStatus.Queued, Result = null, Error = null }
            : job);
    }

    public void Finish(RunningJob running, BenchmarkResult? result, Exception? error, bool stopping)
    {
        lock (gate)
        {
            var status = ResolveStatus(running, error, stopping);

            try
            {
                repository.UpdateJob(running.Job.Id, job => job with
                {
                    Status = status,
                    Result = status == JobStatus.Done ? result : null,
                    Error = status == JobStatus.Failed ? error?.Message : null
                });
            }
            finally
            {
                active = null;
                running.Cancellation.Dispose();
            }
        }
    }

    private static string ResolveStatus(RunningJob running, Exception? error, bool stopping)
    {
        if (error is OperationCanceledException || running.Cancellation.IsCancellationRequested)
        {
            return stopping && !running.CancelledByUser ? JobStatus.Queued : JobStatus.Cancelled;
        }

        return error is null ? JobStatus.Done : JobStatus.Failed;
    }
}

public sealed class RunningJob(Job job, CancellationTokenSource cancellation)
{
    public Job Job { get; } = job;
    public CancellationTokenSource Cancellation { get; } = cancellation;
    public bool CancelledByUser { get; set; }
}
