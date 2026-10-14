using CompressionLab.Web.Services;

namespace CompressionLab.Web.Endpoints;

public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/files", UploadAsync);
        endpoints.MapPost("/api/demo", AddDemoAsync);
        endpoints.MapPost("/api/restore", RestoreAsync);
        return endpoints;
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, FileService files, CancellationToken cancellationToken)
    {
        var name = Uri.UnescapeDataString(request.Headers["X-File-Name"].ToString());
        var file = await files.UploadAsync(request.Body, name, cancellationToken);
        return Results.Json(file);
    }

    private static async Task<IResult> AddDemoAsync(FileService files, CancellationToken cancellationToken)
    {
        await files.AddDemoAsync(cancellationToken);
        return Results.Ok();
    }

    private static async Task<IResult> RestoreAsync(HttpRequest request, ArchiveService archives, CancellationToken cancellationToken)
    {
        var file = await archives.RestoreAsync(request.Body, cancellationToken);
        return new TemporaryDownload(file);
    }

    // A result owns the restored file until streaming ends, including a disconnected client.
    private sealed class TemporaryDownload(RestoredFile file) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            try
            {
                await Results.File(file.Path, "application/octet-stream", file.Name).ExecuteAsync(context);
            }
            finally
            {
                File.Delete(file.Path);
            }
        }
    }
}
