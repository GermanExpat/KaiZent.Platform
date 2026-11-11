namespace KaiZent.Platform.Api;

/// <summary>Einstellungen aus dem Abschnitt <c>Platform</c> (Umgebung: <c>Platform__Jwt__SigningKey</c> …). Geheimnisse nie in appsettings.</summary>
public sealed class PlatformOptions
{
    public const string Section = "Platform";

    public JwtOptions Jwt { get; set; } = new();
    public BootstrapOptions Bootstrap { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();

    /// <summary>Anmeldeversuche je Absender und Minute.</summary>
    public int LoginRequestsPerMinute { get; set; } = 20;

    /// <summary>Neue und zurückgesetzte Passwörter müssen bei der ersten Anmeldung geändert werden.</summary>
    public bool RequireInitialPasswordChange { get; set; } = true;
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "kaizent";
    public string Audience { get; set; } = "kaizent";
    public string SigningKey { get; set; } = "";
    public int LifetimeMinutes { get; set; } = 480;
}

/// <summary>Erstes Konto der Plattform-Administration; wird beim Start angelegt, solange es noch keines gibt.</summary>
public sealed class BootstrapOptions
{
    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }
    public string AdminName { get; set; } = "Plattform-Administration";
}

public sealed class DatabaseOptions
{
    public int CatalogPoolSize { get; set; } = 30;
    public int MaxActiveTenantConnections { get; set; } = 60;
    public int MaxPerTenant { get; set; } = 15;
    public int IdleSeconds { get; set; } = 30;
}
