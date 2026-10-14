using CompressionLab.Web.Models;
using CompressionLab.Web.Services;
using CompressionLab.Web.Storage;

namespace CompressionLab.Web.Endpoints;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/run", Enqueue);
        endpoints.MapPost("/api/queue/pause", TogglePause);
        endpoints.MapPost("/api/jobs/{id}/cancel", Cancel);
        endpoints.MapPost("/api/jobs/{id}/retry", Retry);
        endpoints.MapGet("/api/jobs/{id}/archive", DownloadArchive);
        return endpoints;
    }

    private static IResult Enqueue(RunRequest request, JobQueue queue)
    {
        queue.Enqueue(request);
        return Results.Ok();
    }

    private static IResult TogglePause(JobQueue queue)
    {
        queue.TogglePause();
        return Results.Ok();
    }

    private static IResult Cancel(string id, JobQueue queue)
    {
        queue.Cancel(id);
        return Results.Ok();
    }

    private static IResult Retry(string id, JobQueue queue)
    {
        queue.Retry(id);
        return Results.Ok();
    }

    private static IResult DownloadArchive(string id, LabRepository repository, LabPaths paths)
    {
        var job = repository.Snapshot().Jobs.FirstOrDefault(job => job.Id == id && job.Status == JobStatus.Done);
        return job is null
            ? Results.NotFound()
            : Results.File(paths.Artifact(job.Id), "application/octet-stream", job.Id + ".clcs");
    }
}
