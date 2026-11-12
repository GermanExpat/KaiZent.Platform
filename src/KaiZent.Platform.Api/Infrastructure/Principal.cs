using System.Globalization;
using System.Net;
using System.Security.Claims;
using KaiZent.Platform.Contracts.Security;
using Microsoft.AspNetCore.Http;

namespace KaiZent.Platform.Api.Infrastructure;

public static class Principal
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(PlatformClaims.UserId), out var id) ? id : throw ApiProblem.Forbidden("Kein gültiger Benutzer.");

    public static Guid? TenantId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(PlatformClaims.TenantId), out var id) ? id : null;

    public static string DisplayName(this ClaimsPrincipal user) => user.FindFirstValue(PlatformClaims.Name) ?? "";

    public static string Role(this ClaimsPrincipal user) => user.FindFirstValue(PlatformClaims.Role) ?? "";

    public static int SessionVersion(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(PlatformClaims.SessionVersion), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static string? TokenId(this ClaimsPrincipal user) => user.FindFirstValue(PlatformClaims.TokenId);
}

/// <summary>
/// Absender einer Anfrage für Drosselung und Sperre der Anmeldung: die Adresse des Browsers, die die App im Header
/// <see cref="ClientAddressHeader.Name"/> weiterreicht, sonst die Adresse der Verbindung. Die API ist nur aus dem internen Netz erreichbar.
/// </summary>
public static class ClientAddress
{
    public static string Of(HttpContext http)
    {
        if (IPAddress.TryParse(http.Request.Headers[ClientAddressHeader.Name].ToString(), out var forwarded)) return forwarded.ToString();
        return http.Connection.RemoteIpAddress?.ToString() ?? "unbekannt";
    }
}

public static class RateLimits
{
    public const string Login = "kaizent-login";
}
