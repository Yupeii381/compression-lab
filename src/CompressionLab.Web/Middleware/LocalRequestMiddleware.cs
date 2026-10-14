namespace CompressionLab.Web.Middleware;

public sealed class LocalRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsLocalRequest(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; style-src 'self'; script-src 'self'; object-src 'none'; frame-ancestors 'none'";
        await next(context);
    }

    private static bool IsLocalRequest(HttpRequest request)
    {
        if (request.Host.Host is not ("127.0.0.1" or "localhost" or "::1"))
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 ||
            (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
             uri.Authority == request.Host.Value && uri.Scheme == request.Scheme);
    }
}
