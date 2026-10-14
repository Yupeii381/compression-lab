using System.Text.Json;

namespace CompressionLab.Web.Middleware;

public sealed class ApiExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted && IsClientError(exception))
        {
            context.Response.StatusCode = exception is BadHttpRequestException badRequest
                ? badRequest.StatusCode
                : StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
    }

    private static bool IsClientError(Exception exception) => exception is
        ArgumentException or InvalidDataException or JsonException or BadHttpRequestException or EndOfStreamException;
}
