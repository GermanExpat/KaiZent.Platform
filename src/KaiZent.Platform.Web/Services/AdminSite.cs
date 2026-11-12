using KaiZent.Platform.Contracts.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace KaiZent.Platform.Web.Services;

/// <summary>
/// Eigene Adresse der Plattform-Administration (<see cref="KaiZentWebOptions.AdminHost"/>). Ist sie gesetzt, melden sich dort nur
/// Plattform-Konten an und auf der normalen Adresse nur Konten der Betriebe; die Cookies gelten je Adresse. Ohne Einstellung bleibt alles
/// auf einer Adresse.
/// </summary>
public sealed class AdminSite(IOptions<KaiZentWebOptions> options)
{
    public string? Host { get; } = options.Value.AdminHost is { Length: > 0 } h ? h.Trim().ToLowerInvariant() : null;

    public bool IsAdminHost(HttpContext ctx) => Host is not null && string.Equals(ctx.Request.Host.Value, Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Anmeldebereich für die API: mit eigener Admin-Adresse admin/tenant, sonst null (beide).</summary>
    public string? AreaOf(HttpContext ctx) => Host is null ? null : IsAdminHost(ctx) ? LoginAreas.Admin : LoginAreas.Tenant;

    /// <summary>Trennt Admin- und Betriebsadresse; läuft nach der Authentifizierung.</summary>
    public async Task<bool> HandleAsync(HttpContext ctx)
    {
        if (Host is null) return false;
        var path = ctx.Request.Path;
        var user = ctx.User;
        var signedIn = user.Identity?.IsAuthenticated == true;
        var isPlatform = user.IsInRole(Roles.PlatformAdmin);
        if (IsAdminHost(ctx))
        {
            if (signedIn && !isPlatform)
            {
                await ctx.SignOutAsync();
                ctx.Response.Redirect("/account/login");
                return true;
            }
            if (isPlatform && (path == "/" || path == ""))
            {
                ctx.Response.Redirect("/admin");
                return true;
            }
            return false;
        }
        if (path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Redirect($"{ctx.Request.Scheme}://{Host}{ctx.Request.PathBase}{path}{ctx.Request.QueryString}");
            return true;
        }
        if (signedIn && isPlatform)
        {
            await ctx.SignOutAsync();
            ctx.Response.Redirect("/account/login");
            return true;
        }
        return false;
    }
}
