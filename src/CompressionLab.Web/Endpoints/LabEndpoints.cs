using System.Runtime.InteropServices;
using CompressionLab.Core;
using CompressionLab.Web.Storage;

namespace CompressionLab.Web.Endpoints;

public static class LabEndpoints
{
    public static IEndpointRouteBuilder MapLabEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/state", GetState);
        return endpoints;
    }

    private static IResult GetState(LabRepository repository, CodecRegistry registry)
    {
        var snapshot = repository.Snapshot();
        return Results.Json(new
        {
            files = snapshot.Files,
            jobs = snapshot.Jobs,
            paused = snapshot.Paused,
            codecs = registry.Descriptors,
            diagnostics = registry.Diagnostics,
            runtime = RuntimeInformation.FrameworkDescription
        }, ArchiveEngine.Json);
    }
}
