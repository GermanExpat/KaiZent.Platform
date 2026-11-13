namespace KaiZent.Platform.Contracts.Security;

/// <summary>
/// Rollen. Die Plattform-Administration legt Mandanten an und gehört zu keinem; sie sieht nie Inhalte eines Mandanten.
/// Im Mandanten: die Administration verwaltet die Benutzer und hat alle Rechte; Mitglieder (Büro, Trainer) arbeiten mit allen Fachdaten;
/// Stallpersonal (<see cref="Staff"/>) mit den täglichen Abläufen, aber ohne Büro-Bereiche (Verträge, Kontakte, Nachrichten);
/// der Tierarzt (<see cref="Vet"/>) liest nur; Kunden (<see cref="Owner"/>, z. B. Einsteller) sehen nur ihr eigenes Portal.
/// Welche Fachroute wer nutzen darf, legt das Produkt über <c>TenantAccess</c> fest.
/// </summary>
public static class Roles
{
    public const string PlatformAdmin = "platform_admin";
    public const string TenantAdmin = "tenant_admin";
    public const string Member = "member";
    public const string Staff = "staff";
    public const string Vet = "vet";
    public const string Owner = "owner";

    /// <summary>Rollen, die ein Mandant selbst vergeben darf.</summary>
    public static readonly IReadOnlyList<string> TenantRoles = [TenantAdmin, Member, Staff, Vet, Owner];

    public static bool IsTenantRole(string? role) => role is TenantAdmin or Member or Staff or Vet or Owner;

    /// <summary>Team des Betriebs: arbeitet mit den Fachdaten.</summary>
    public static bool IsTeam(string? role) => role is TenantAdmin or Member or Staff;

    /// <summary>Büro: Team ohne Stallpersonal (Verträge, Kontakte, Nachrichten).</summary>
    public static bool IsOffice(string? role) => role is TenantAdmin or Member;

    /// <summary>Für <c>[Authorize(Roles = ...)]</c>: Team des Betriebs.</summary>
    public const string TeamList = TenantAdmin + "," + Member + "," + Staff;

    /// <summary>Für <c>[Authorize(Roles = ...)]</c>: Büro.</summary>
    public const string OfficeList = TenantAdmin + "," + Member;

    /// <summary>Für <c>[Authorize(Roles = ...)]</c>: Team und Tierarzt (lesend).</summary>
    public const string TeamAndVetList = TeamList + "," + Vet;
}

/// <summary>Claim-Typen des Zugriffstokens.</summary>
public static class PlatformClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string TenantId = "kz_tenant";
    /// <summary>Stand der Sitzungen des Kontos; ändert sich bei Passwortwechsel und Sperre, ältere Tokens gelten dann nicht mehr.</summary>
    public const string SessionVersion = "kz_sv";
    /// <summary>Kennung des Tokens (für die Abmeldung).</summary>
    public const string TokenId = "jti";
}

/// <summary>
/// Header, mit dem die App der API die Adresse des Browsers mitteilt (die API sieht sonst nur die App). Die API ist nur aus dem internen
/// Netz erreichbar; sie nutzt den Wert nur für die Drosselung der Anmeldung.
/// </summary>
public static class ClientAddressHeader
{
    public const string Name = "X-KaiZent-Client-Ip";
}
