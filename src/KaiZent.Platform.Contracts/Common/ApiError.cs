namespace KaiZent.Platform.Contracts.Common;

/// <summary>
/// Einheitliche Fehlerantwort der API. <see cref="Code"/> ist stabil und wird von der App ausgewertet (auch für die Übersetzung);
/// <see cref="Message"/> ist ein deutscher Klartext als Rückfall. <see cref="Errors"/> enthält Feldfehler (Feldname → Meldungen).
/// </summary>
public sealed record ApiError(string Code, string Message, IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>Fehlercodes der Plattform. Produkte dürfen eigene Codes ergänzen, aber diese nicht umdeuten.</summary>
public static class ErrorCodes
{
    public const string Validation = "validation";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    /// <summary>Der Datensatz wurde inzwischen von jemand anderem geändert (veraltete Version).</summary>
    public const string Stale = "stale";
    public const string Forbidden = "forbidden";
    public const string Unauthorized = "unauthorized";
    public const string InvalidCredentials = "invalid_credentials";
    public const string AccountLocked = "account_locked";
    public const string TenantInactive = "tenant_inactive";
    /// <summary>Passwort richtig, aber falsche Anmeldeadresse (Plattform-Konto auf der Mandanten-Adresse oder umgekehrt).</summary>
    public const string WrongLoginArea = "wrong_login_area";
    /// <summary>Das Passwort muss erst geändert werden (vorläufiges Passwort).</summary>
    public const string PasswordChangeRequired = "password_change_required";
    public const string Internal = "internal";
    /// <summary>Datenbank oder ein angebundenes System ist kurz nicht erreichbar; ein erneuter Versuch kann gelingen.</summary>
    public const string Unavailable = "unavailable";
}
