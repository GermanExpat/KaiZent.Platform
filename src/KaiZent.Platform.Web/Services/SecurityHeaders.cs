using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KaiZent.Platform.Web.Services;

/// <summary>
/// Schutz-Header für jede Antwort: keine Einbettung in fremde Seiten, kein Raten des Inhaltstyps, keine Adresse im Referer an fremde Seiten.
/// Die CSP für HTML beschränkt Einbettung, Formularziele, base und Plugins; Skripte und Styles von Blazor und DevExpress bleiben unberührt.
/// </summary>
public static class SecurityHeaders
{
    public const string HtmlPolicy = "frame-ancestors 'self'; base-uri 'self'; form-action 'self'; object-src 'none'";

    public static IApplicationBuilder UseKaiZentSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        ctx.Response.OnStarting(() =>
        {
            var headers = ctx.Response.Headers;
            SetIfMissing(headers, "X-Content-Type-Options", "nosniff");
            SetIfMissing(headers, "X-Frame-Options", "SAMEORIGIN");
            SetIfMissing(headers, "Referrer-Policy", "same-origin");
            SetIfMissing(headers, "Permissions-Policy", "camera=(self), microphone=(), geolocation=(), payment=(), usb=()");
            if (ctx.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            {
                SetIfMissing(headers, "Content-Security-Policy", HtmlPolicy);
                SetIfMissing(headers, "Cross-Origin-Opener-Policy", "same-origin");
            }
            return Task.CompletedTask;
        });
        await next(ctx);
    });

    private static void SetIfMissing(IHeaderDictionary headers, string name, string value)
    {
        if (!headers.ContainsKey(name)) headers[name] = value;
    }
}
