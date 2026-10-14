using CompressionLab.Web.Services;

namespace CompressionLab.Web.Endpoints;

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/export.json", (ExportService exports) =>
            Results.File(exports.Json(), "application/json", "compression-results.json"));
        endpoints.MapGet("/api/export.csv", (ExportService exports) =>
            Results.File(exports.Csv(), "text/csv; charset=utf-8", "compression-results.csv"));
        return endpoints;
    }
}
