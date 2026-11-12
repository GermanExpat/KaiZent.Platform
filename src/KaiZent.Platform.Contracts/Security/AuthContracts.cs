namespace KaiZent.Platform.Contracts.Security;

/// <summary>
/// Anmeldung. <see cref="Area"/>: auf welcher Adresse angemeldet wird (<see cref="LoginAreas"/>). Ist sie gesetzt, gelingt die Anmeldung nur mit
/// einem passenden Konto (sonst 403 <c>wrong_login_area</c>); null = beide.
/// </summary>
public sealed record LoginRequest(string Email, string Password, string? Area = null);

public static class LoginAreas
{
    public const string Admin = "admin";
    public const string Tenant = "tenant";

    /// <summary>Passt das Konto zur Anmeldeadresse?</summary>
    public static bool Allows(string? area, string role) => area switch
    {
        Admin => role == Roles.PlatformAdmin,
        Tenant => role != Roles.PlatformAdmin,
        _ => true,
    };
}

/// <summary>Ergebnis einer Anmeldung. Bei <see cref="MustChangePassword"/> lässt die API bis zur Änderung nur die Passwortänderung zu.</summary>
public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, CurrentUser User, bool MustChangePassword = false);

/// <summary>Der angemeldete Benutzer, mit seinen Einstellungen (damit die App sofort im richtigen Farbschema und in der richtigen Sprache startet).</summary>
public sealed record CurrentUser(Guid Id, string Email, string DisplayName, string Role, Guid? TenantId, string? TenantName,
    IReadOnlyList<string> TenantModules, Preferences.UserPreferences Preferences);

/// <summary>Eigenes Passwort ändern. Beendet alle anderen Anmeldungen; die Antwort enthält die neue Anmeldung.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
