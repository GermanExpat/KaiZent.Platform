using Dapper;
using KaiZent.Platform.Api.Settings;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Preferences;
using KaiZent.Platform.Contracts.Security;
using KaiZent.Platform.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KaiZent.Platform.Api.Infrastructure;

/// <summary>Beim Start: Katalog und alle Mandanten-Datenbanken migrieren, erstes Konto der Plattform-Administration anlegen, Systemeinstellungen laden.</summary>
public sealed class StartupTasks(CatalogDb catalog, TenantProvisioner provisioner, PlatformSettings settings, IOptions<PlatformOptions> options,
    ILogger<StartupTasks> log)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        await new MigrationRunner(catalog.ConnectionString, [MigrationSet.PlatformCatalog], m => log.LogInformation("Katalog: {Migration}", m)).MigrateAsync(ct);

        List<string> tenants;
        await using (var conn = await catalog.OpenAsync(ct))
            tenants = (await conn.QueryAsync<string>(new CommandDefinition("select database_name from catalog.tenant order by created_at", cancellationToken: ct))).ToList();
        foreach (var db in tenants) await provisioner.MigrateAsync(db, ct);

        await BootstrapAdminAsync(ct);
        await settings.RefreshAsync(ct);
    }

    private async Task BootstrapAdminAsync(CancellationToken ct)
    {
        var b = options.Value.Bootstrap;
        await using var conn = await catalog.OpenAsync(ct);
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from catalog.app_user where role = @Role)", new { Role = Roles.PlatformAdmin }, cancellationToken: ct));
        if (exists) return;
        if (!Validation.IsEmail(b.AdminEmail) || !Validation.IsStrongPassword(b.AdminPassword))
        {
            log.LogWarning("Noch keine Plattform-Administration: Platform:Bootstrap:AdminEmail und AdminPassword (mind. 12 Zeichen mit Ziffern) setzen.");
            return;
        }
        var id = Guid.CreateVersion7();
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into catalog.app_user (id, email, display_name, password_hash, role, must_change_password)
            values (@Id, @Email, @Name, @Hash, @Role, @MustChange);
            insert into catalog.user_preferences (user_id, theme, language) values (@Id, @Theme, @Language);
            """, new { Id = id, Email = b.AdminEmail, Name = b.AdminName, Hash = Passwords.Hash(b.AdminPassword!), Role = Roles.PlatformAdmin,
                MustChange = options.Value.RequireInitialPasswordChange, Theme = ThemeCatalog.Default, Language = Languages.Default }, cancellationToken: ct));
        log.LogInformation("Konto der Plattform-Administration {Email} angelegt.", b.AdminEmail);
    }
}
