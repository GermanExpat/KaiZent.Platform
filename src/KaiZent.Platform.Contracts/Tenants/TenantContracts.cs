namespace KaiZent.Platform.Contracts.Tenants;

/// <summary>Mandant aus Sicht der Plattform-Administration: nur Stammdaten und Zählwerte, nie Inhalte.</summary>
public sealed record TenantSummary(Guid Id, string Name, bool IsActive, IReadOnlyList<string> Modules, string? ContactEmail,
    int UserCount, DateTimeOffset CreatedAt);

/// <summary>
/// Neuer Mandant. Dabei entstehen die eigene Datenbank und das erste Administrator-Konto. <see cref="Modules"/> sind die Fachmodule,
/// die das Produkt kennt (bei LMS z. B. <c>equine</c>, <c>livestock</c>).
/// </summary>
public sealed record CreateTenantRequest(string Name, IReadOnlyList<string> Modules, string AdminEmail, string AdminDisplayName,
    string AdminInitialPassword, string? ContactEmail = null);

public sealed record CreateTenantResponse(TenantSummary Tenant, Guid AdminUserId);

public sealed record UpdateTenantRequest(string Name, IReadOnlyList<string> Modules, string? ContactEmail);

public sealed record SetActiveRequest(bool IsActive);

/// <summary>Benutzer eines Mandanten aus Sicht der Mandanten-Administration.</summary>
public sealed record TenantUser(Guid Id, string Email, string DisplayName, string Role, bool IsActive, DateTimeOffset? LastLoginAt, bool TwoFactorEnabled);

/// <summary>Neues Konto im eigenen Mandanten; das Passwort ist vorläufig und muss bei der ersten Anmeldung geändert werden.</summary>
public sealed record CreateTenantUserRequest(string Email, string DisplayName, string Role, string InitialPassword);

public sealed record UpdateTenantUserRequest(string DisplayName, string Role);

/// <summary>Vorläufiges Passwort setzen; der Benutzer muss es bei der nächsten Anmeldung ändern.</summary>
public sealed record ResetPasswordRequest(string NewPassword);

/// <summary>Einstellungen des Mandanten, die für alle seine Benutzer gelten.</summary>
public sealed record TenantSettings(string DefaultTheme, string DefaultLanguage, bool RequireTwoFactor = false);
