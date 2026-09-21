using Microsoft.Extensions.Primitives;

namespace AmwalECR.WebExample.Hosting;

/// <summary>
/// The cheap protections for an app that has no authentication and can take
/// payments. They do not replace authentication (see the README) — they keep a
/// page in another tab of the operator's browser from driving it.
/// </summary>
public static class SecurityMiddleware
{
    // No inline script or style anywhere, so the policy needs no exceptions.
    private const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; " +
        "form-action 'self'; frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            // Terminals, results and the log are operator data: never cached.
            if (context.Request.Path.StartsWithSegments("/api")) headers["Cache-Control"] = "no-store";
            await next();
        });

    /// <summary>
    /// For <c>/api</c>: refuses requests another origin made, and mutating requests
    /// that are not JSON.
    /// <para>
    /// No CORS is configured, so a browser will not let another site read a
    /// response. That does not stop it <em>sending</em> a simple cross-site form
    /// POST, so mutating calls must carry <c>Content-Type: application/json</c>
    /// (which forces a preflight that fails) and an <c>Origin</c>, when the browser
    /// sends one, must be this host. Host names are limited by <c>AllowedHosts</c>,
    /// which also defeats DNS rebinding.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseApiGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (!request.Path.StartsWithSegments("/api"))
            {
                await next();
                return;
            }

            if (!SameOrigin(request))
            {
                await Reject(context, StatusCodes.Status403Forbidden, "Cross-origin requests are not accepted");
                return;
            }

            if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !IsJson(request))
            {
                await Reject(context, StatusCodes.Status415UnsupportedMediaType, "Use Content-Type: application/json");
                return;
            }

            await next();
        });

    private static bool SameOrigin(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length != 0 && fetchSite is not ("same-origin" or "none")) return false;

        StringValues origin = request.Headers.Origin;
        if (StringValues.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri)) return false;

        // Uri.Authority leaves out a scheme's default port, as the Host header does.
        return string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJson(HttpRequest request) =>
        request.ContentType is { } type
        && Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(type, out var media)
        && string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);

    private static Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }
}
