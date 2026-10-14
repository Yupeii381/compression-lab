using CompressionLab.Core;
using CompressionLab.Web.Configuration;
using CompressionLab.Web.Storage;

namespace CompressionLab.Web.Services;

public sealed class LabWorker(
    JobQueue queue,
    LabRepository repository,
    LabPaths paths,
    ArchiveEngine engine,
    ILogger<LabWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var running = queue.TryStartNext(stoppingToken);
            if (running is null)
            {
                try
                {
                    await Task.Delay(LabLimits.QueuePollInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            await ExecuteJobAsync(running, stoppingToken);
        }
    }

    private async Task ExecuteJobAsync(RunningJob running, CancellationToken stoppingToken)
    {
        BenchmarkResult? result = null;
        Exception? error = null;

        try
        {
            var job = running.Job;
            var file = repository.GetFile(job.FileId);
            var artifact = paths.Artifact(job.Id);

            // A recovered job starts from scratch; only its own unfinished artifact is removed.
            if (File.Exists(artifact))
            {
                File.Delete(artifact);
            }

            result = await Task.Run(() => engine.Compress(
                paths.Blob(file.Id), artifact, file.Name, job.Chain.Steps,
                job.BlockSize, job.Repeats, job.Warmup, running.Cancellation.Token), stoppingToken);
        }
        catch (Exception exception)
        {
            error = exception;
            if (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Job {JobId} failed", running.Job.Id);
            }
        }
        finally
        {
            queue.Finish(running, result, error, stoppingToken.IsCancellationRequested);
        }
    }
}
