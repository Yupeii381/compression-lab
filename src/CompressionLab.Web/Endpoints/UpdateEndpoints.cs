using CompressionLab.Web.Updates;

namespace CompressionLab.Web.Endpoints;

public static class UpdateEndpoints
{
    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/health", () => Results.Ok(new { version = UpdateRules.CurrentVersion, processId = Environment.ProcessId }));
        endpoints.MapGet("/api/updates", (UpdateService updates) => Results.Ok(updates.Status));
        endpoints.MapPost("/api/updates/check", async (UpdateService updates, CancellationToken cancellationToken) =>
        {
            await updates.CheckAsync(cancellationToken);
            return Results.Ok(updates.Status);
        });
        endpoints.MapPost("/api/updates/install", (UpdateService updates) =>
        {
            updates.StartInstallation();
            return Results.Accepted("/api/updates");
        });
        return endpoints;
    }
}
