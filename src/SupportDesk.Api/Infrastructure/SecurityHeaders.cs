namespace SupportDesk.Api.Infrastructure;

public static class SecurityHeadersExtensions
{
    // The interactive API reference (Development only) needs scripts and styles, so it keeps a looser policy.
    private static readonly string[] DocsPaths = ["/scalar", "/openapi"];

    /// <summary>
    /// Defence-in-depth response headers. The API only returns JSON, so the content policy can be as strict as
    /// "load nothing", and nothing it returns should ever be cached or framed.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

                var path = context.Request.Path;
                if (!DocsPaths.Any(p => path.StartsWithSegments(p)))
                {
                    headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                }

                if (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs"))
                {
                    headers["Cache-Control"] = "no-store";
                }

                return Task.CompletedTask;
            });

            await next(context);
        });
}
