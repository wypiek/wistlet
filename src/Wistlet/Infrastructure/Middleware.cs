using Wistlet.Services;

namespace Wistlet.Infrastructure;

public static class MiddlewareExtensions
{
    /// <summary>Adds conservative security headers to every response.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
            "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
        await next();
    });

    /// <summary>While maintenance mode is on, only staff (and the login page) get through.</summary>
    public static IApplicationBuilder UseMaintenanceMode(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var settings = ctx.RequestServices.GetRequiredService<SiteSettingsService>();
        if (!settings.Current.MaintenanceMode || ctx.User.IsStaff())
        {
            await next();
            return;
        }

        var path = ctx.Request.Path;
        string[] allowed = ["/account/login", "/account/logout", "/maintenance", "/lib", "/css", "/js", "/img", "/avatar", "/favicon.ico", "/healthz"];
        if (allowed.Any(a => path.StartsWithSegments(a, StringComparison.OrdinalIgnoreCase)))
        {
            await next();
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Request.Path = "/maintenance";
        ctx.Request.Method = HttpMethods.Get;
        await next();
    });
}
