using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Security;
using KaiZent.Platform.Contracts.Tenants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using KaiZent.Platform.Api.Documents;
using KaiZent.Platform.Api.Settings;
using KaiZent.Platform.Contracts.Settings;
using Npgsql;

namespace KaiZent.Platform.Tests;

/// <summary>Integrationstest, der nur mit PostgreSQL läuft (Umgebungsvariable KAIZENT_TEST_POSTGRES).</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(ApiFixture.ServerConnection)) Skip = "KAIZENT_TEST_POSTGRES ist nicht gesetzt.";
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;

/// <summary>
/// Startet den Beispiel-Gastgeber gegen eine frische Wegwerf-Katalogdatenbank (kzp_test_…). Alle Mandanten-Datenbanken, die die Tests
/// anlegen, und der Katalog werden am Ende wieder entfernt.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly string? ServerConnection = Environment.GetEnvironmentVariable("KAIZENT_TEST_POSTGRES");

    public const string AdminEmail = "plattform@test.kaizent.de";
    public const string AdminPassword = "Plattform2026Test";
    public const string TenantPassword = "Mandant2026Start";

    /// <summary>Wegwerf-Ordner für den SecretBox-Schlüssel.</summary>
    public string KeysPath { get; } = Path.Combine(Path.GetTempPath(), "kzp_test_keys_" + Guid.NewGuid().ToString("N")[..12]);

    private readonly string _catalogName = "kzp_test_" + Guid.NewGuid().ToString("N")[..12];

    public string CatalogConnection => new NpgsqlConnectionStringBuilder(ServerConnection)
    {
        Database = _catalogName, ConnectionIdleLifetime = 2, ConnectionPruningInterval = 1,
    }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Catalog", CatalogConnection);
        builder.UseSetting("Platform:Jwt:SigningKey", "test-signing-key-" + new string('x', 40));
        builder.UseSetting("Platform:Bootstrap:AdminEmail", AdminEmail);
        builder.UseSetting("Platform:Bootstrap:AdminPassword", AdminPassword);
        builder.UseSetting("Platform:LoginRequestsPerMinute", "10000");
        builder.UseSetting("Platform:Database:IdleSeconds", "2");
        // Pflicht zum Passwortwechsel prüft ein eigener Test; die übrigen arbeiten mit dem Startpasswort
        builder.UseSetting("Platform:RequireInitialPasswordChange", "false");
        // Pflicht für die Plattform-Administration prüft ein eigener Test
        builder.UseSetting("Platform:RequirePlatformTwoFactor", "false");
        builder.UseSetting("Platform:KeysPath", KeysPath);
        // E-Mails der Plattform (Rechnungen) landen in einem Postfach im Speicher
        builder.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IPlatformMailer>(Mailer)));
        // KI-Verbindungstest ohne Netz: merkt sich, womit er aufgerufen wurde
        builder.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IAiConnectionTester>(AiTester)));
    }

    public CapturingMailer Mailer { get; } = new();

    public RecordingAiTester AiTester { get; } = new();

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerConnection)) return;
        await using var conn = new NpgsqlConnection(ServerConnection);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"create database \"{_catalogName}\"");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(KeysPath)) Directory.Delete(KeysPath, recursive: true);
        if (string.IsNullOrWhiteSpace(ServerConnection)) return;
        NpgsqlConnection.ClearAllPools();
        var tenantDbs = new List<string>();
        try
        {
            await using var catalog = new NpgsqlConnection(CatalogConnection);
            await catalog.OpenAsync();
            tenantDbs.AddRange(await catalog.QueryAsync<string>("select database_name from catalog.tenant"));
        }
        catch (PostgresException) { }
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(ServerConnection);
        await conn.OpenAsync();
        foreach (var db in tenantDbs.Append(_catalogName))
            await conn.ExecuteAsync($"drop database if exists \"{db}\" with (force)");
    }

    public async Task<(HttpClient Client, LoginResponse Login)> LoginAsync(string email, string password, string? area = LoginAreas.Tenant)
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync(PlatformRoutes.Login, new LoginRequest(email, password, area));
        resp.EnsureSuccessStatusCode();
        var login = (await resp.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return (client, login);
    }

    public Task<(HttpClient Client, LoginResponse Login)> LoginAdminAsync() => LoginAsync(AdminEmail, AdminPassword, LoginAreas.Admin);

    /// <summary>Legt einen Mandanten mit eigener Administration an und meldet diese an.</summary>
    public async Task<(HttpClient Client, LoginResponse Login, TenantSummary Tenant)> NewTenantAsync(params string[] modules)
    {
        var (admin, _) = await LoginAdminAsync();
        var email = $"admin-{Guid.NewGuid():N}@hof.test";
        var resp = await admin.PostAsJsonAsync(PlatformRoutes.PlatformTenants,
            new CreateTenantRequest("Hof " + Guid.NewGuid().ToString("N")[..6], modules.Length == 0 ? ["notes"] : modules, email, "Hof-Admin", TenantPassword));
        resp.EnsureSuccessStatusCode();
        var created = (await resp.Content.ReadFromJsonAsync<CreateTenantResponse>())!;
        var (client, login) = await LoginAsync(email, TenantPassword);
        return (client, login, created.Tenant);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(CatalogConnection);
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }
}

/// <summary>Postfach im Speicher für die Tests.</summary>
public sealed class CapturingMailer : IPlatformMailer
{
    private readonly List<(string To, string Subject, MailAttachment? Attachment)> _sent = [];

    public bool IsConfigured => true;

    public IReadOnlyList<(string To, string Subject, MailAttachment? Attachment)> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
    }

    public Task SendAsync(string to, string subject, string body, MailAttachment? attachment, string? replyTo, CancellationToken ct)
    {
        lock (_sent) _sent.Add((to, subject, attachment));
        return Task.CompletedTask;
    }
}

/// <summary>KI-Verbindungstest ohne Netz.</summary>
public sealed class RecordingAiTester : IAiConnectionTester
{
    public EffectiveAi? Last { get; private set; }

    public Task<SettingsTestResult> TestAsync(EffectiveAi ai, CancellationToken ct)
    {
        Last = ai;
        return Task.FromResult(ai.Active is { } p ? new SettingsTestResult(true, p.Name) : new SettingsTestResult(false, "keine KI"));
    }
}
