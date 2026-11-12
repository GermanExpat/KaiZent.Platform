using System.Security.Claims;
using KaiZent.Platform.Contracts.Preferences;
using KaiZent.Platform.Contracts.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;

namespace KaiZent.Platform.Web.Services;

/// <summary>Claims im Anmelde-Cookie der App. Das Cookie ist per Data Protection verschlüsselt und trägt das Zugriffstoken der API.</summary>
public static class AppClaims
{
    public const string AccessToken = "kz:access_token";
    public const string TenantId = "kz:tenant";
    public const string TenantName = "kz:tenant_name";
    /// <summary>Freigeschaltete Module des Mandanten, durch Komma getrennt.</summary>
    public const string Modules = "kz:modules";
    public const string Theme = "kz:theme";
    public const string Language = "kz:lang";
    public const string Density = "kz:density";
    /// <summary>"1": vorläufiges Passwort, bis zur Änderung nur die Passwortseite.</summary>
    public const string MustChangePassword = "kz:must_change";

    public static ClaimsPrincipal PrincipalOf(LoginResponse login) => PrincipalOf(login.User, login.AccessToken, login.MustChangePassword);

    public static ClaimsPrincipal PrincipalOf(CurrentUser u, string token, bool mustChangePassword)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new(ClaimTypes.Name, u.DisplayName),
            new(ClaimTypes.Email, u.Email),
            new(ClaimTypes.Role, u.Role),
            new(AccessToken, token),
            new(Modules, string.Join(',', u.TenantModules)),
            new(Theme, ThemeCatalog.Resolve(u.Preferences.Theme).Key),
            new(Language, Languages.IsKnown(u.Preferences.Language) ? u.Preferences.Language : Languages.Default),
            new(Density, Densities.IsKnown(u.Preferences.Density) ? u.Preferences.Density : Densities.Normal),
        };
        if (u.TenantId is { } t) claims.Add(new(TenantId, t.ToString()));
        if (u.TenantName is { } n) claims.Add(new(TenantName, n));
        if (mustChangePassword) claims.Add(new(MustChangePassword, "1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static UserPreferences PreferencesOf(ClaimsPrincipal user) => new(
        ThemeCatalog.Resolve(user.FindFirstValue(Theme)).Key,
        Languages.IsKnown(user.FindFirstValue(Language)) ? user.FindFirstValue(Language)! : Languages.Default,
        Densities.IsKnown(user.FindFirstValue(Density)) ? user.FindFirstValue(Density)! : Densities.Normal);

    public static IReadOnlyList<string> ModulesOf(ClaimsPrincipal user) =>
        (user.FindFirstValue(Modules) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Der angemeldete Benutzer der App und sein API-Token. In Komponenten kommt er aus dem <see cref="AuthenticationStateProvider"/>, in
/// Endpunkten (Formular-Posts) aus dem HTTP-Kontext.
/// </summary>
public sealed class AuthSession(AuthenticationStateProvider auth, IHttpContextAccessor http)
{
    public async Task<string?> GetTokenAsync() => (await GetUserAsync()).FindFirstValue(AppClaims.AccessToken);

    public async Task<ClaimsPrincipal> GetUserAsync()
    {
        try
        {
            return (await auth.GetAuthenticationStateAsync()).User;
        }
        catch (InvalidOperationException)
        {
            // außerhalb des Renderns (Minimal-API-Endpunkt): Benutzer der laufenden Anfrage
            return http.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        }
    }

    public async Task<UserPreferences> GetPreferencesAsync() => AppClaims.PreferencesOf(await GetUserAsync());

    public async Task<bool> HasModuleAsync(string module) => AppClaims.ModulesOf(await GetUserAsync()).Contains(module);
}
