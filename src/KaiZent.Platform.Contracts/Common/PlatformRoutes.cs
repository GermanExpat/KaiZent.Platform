namespace KaiZent.Platform.Contracts.Common;

/// <summary>Routen der Plattform. Produkte legen ihre Fachrouten unter eigenen Präfixen an, nie unter diesen.</summary>
public static class PlatformRoutes
{
    public const string Health = "/api/health";

    public const string Login = "/api/auth/login";
    public const string Me = "/api/auth/me";
    public const string Logout = "/api/auth/logout";
    public const string ChangePassword = "/api/auth/password";

    /// <summary>Zweiter Schritt der Anmeldung (Code oder Wiederherstellungscode zur Challenge).</summary>
    public const string LoginTwoFactor = "/api/auth/login/2fa";
    /// <summary>Einrichten während der Anmeldung, wenn die Zwei-Faktor-Anmeldung Pflicht ist.</summary>
    public const string LoginTwoFactorSetup = "/api/auth/login/2fa/setup";
    public const string LoginTwoFactorEnable = "/api/auth/login/2fa/enable";
    /// <summary>Zwei-Faktor-Anmeldung des eigenen Kontos.</summary>
    public const string TwoFactor = "/api/auth/2fa";
    public const string TwoFactorSetup = "/api/auth/2fa/setup";
    public const string TwoFactorEnable = "/api/auth/2fa/enable";
    public const string TwoFactorDisable = "/api/auth/2fa/disable";

    /// <summary>Eigene Einstellungen (Farbschema, Sprache, Dichte).</summary>
    public const string MyPreferences = "/api/me/preferences";
    /// <summary>Auswahl der Farbschemata und Sprachen.</summary>
    public const string PreferenceOptions = "/api/me/preferences/options";

    /// <summary>Plattform-Administration: Mandanten (Betriebe, Büros …).</summary>
    public const string PlatformTenants = "/api/platform/tenants";
    public static string PlatformTenant(Guid id) => $"{PlatformTenants}/{id}";
    public static string PlatformTenantStatus(Guid id) => $"{PlatformTenants}/{id}/status";

    /// <summary>Mandanten-Administration: Benutzer des eigenen Mandanten.</summary>
    public const string TenantUsers = "/api/tenant/users";
    public static string TenantUser(Guid id) => $"{TenantUsers}/{id}";
    public static string TenantUserStatus(Guid id) => $"{TenantUsers}/{id}/status";
    public static string TenantUserPassword(Guid id) => $"{TenantUsers}/{id}/password";

    /// <summary>Einstellungen des eigenen Mandanten (z. B. Standard-Farbschema für neue Benutzer).</summary>
    public const string TenantSettings = "/api/tenant/settings";
}
