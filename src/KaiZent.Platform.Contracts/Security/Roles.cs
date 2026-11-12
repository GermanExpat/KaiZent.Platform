namespace KaiZent.Platform.Contracts.Security;

/// <summary>
/// Rollen. Die Plattform-Administration legt Mandanten an und gehört zu keinem; sie sieht nie Inhalte eines Mandanten.
/// Mandanten-Administratoren verwalten die Benutzer ihres Mandanten und haben dort alle Rechte. Mitglieder arbeiten mit den Rechten,
/// die das Produkt und die Mandanten-Administration festlegen.
/// </summary>
public static class Roles
{
    public const string PlatformAdmin = "platform_admin";
    public const string TenantAdmin = "tenant_admin";
    public const string Member = "member";

    /// <summary>Rollen, die ein Mandant selbst vergeben darf.</summary>
    public static readonly IReadOnlyList<string> TenantRoles = [TenantAdmin, Member];

    public static bool IsTenantRole(string? role) => role is TenantAdmin or Member;
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
