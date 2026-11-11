using Dapper;
using KaiZent.Platform.Contracts.Preferences;
using KaiZent.Platform.Contracts.Security;
using KaiZent.Platform.Data;
using Microsoft.Extensions.Logging;

namespace KaiZent.Platform.Api.Infrastructure;

/// <summary>
/// Legt Mandanten an und hält ihre Datenbanken aktuell. Anlage: Katalogzeile, eigene Datenbank, Migrationen (Plattform, dann Produkt),
/// erstes Administrator-Konto. Scheitert ein Schritt, wird alles wieder entfernt, damit kein halber Mandant zurückbleibt.
/// </summary>
public sealed class TenantProvisioner(CatalogDb catalog, TenantDatabases databases, PlatformProduct product, ILogger<TenantProvisioner> log)
{
    public sealed record Created(Guid TenantId, Guid AdminUserId, string DatabaseName);

    public async Task<Created> CreateAsync(string name, IReadOnlyList<string> modules, string? contactEmail, string adminEmail, string adminName,
        string adminPasswordHash, bool mustChangePassword, CancellationToken ct)
    {
        var tenantId = Guid.CreateVersion7();
        var adminId = Guid.CreateVersion7();
        var dbName = databases.NameFor(tenantId);
        await using (var conn = await catalog.OpenAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                insert into catalog.tenant (id, name, database_name, modules, contact_email) values (@Id, @Name, @Db, @Modules, @Contact)
                """, new { Id = tenantId, Name = name.Trim(), Db = dbName, Modules = modules.ToArray(), Contact = contactEmail }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition("""
                insert into catalog.app_user (id, tenant_id, email, display_name, password_hash, role, must_change_password)
                values (@Id, @Tenant, @Email, @Name, @Hash, @Role, @MustChange)
                """, new { Id = adminId, Tenant = tenantId, Email = adminEmail.Trim(), Name = adminName.Trim(), Hash = adminPasswordHash,
                    Role = Roles.TenantAdmin, MustChange = mustChangePassword }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition("""
                insert into catalog.user_preferences (user_id, theme, language) values (@Id, @Theme, @Language)
                """, new { Id = adminId, Theme = ThemeCatalog.Default, Language = Languages.Default }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        try
        {
            await databases.CreateAsync(catalog.ConnectionString, dbName, ct);
            await MigrateAsync(dbName, ct);
        }
        catch (Exception e)
        {
            log.LogError(e, "Anlage des Mandanten {Tenant} fehlgeschlagen, wird zurückgenommen.", tenantId);
            try { await databases.DropAsync(catalog.ConnectionString, dbName, CancellationToken.None); } catch (Exception drop) { log.LogWarning(drop, "Datenbank {Db} nicht entfernt.", dbName); }
            await using var conn = await catalog.OpenAsync(CancellationToken.None);
            await conn.ExecuteAsync("delete from catalog.app_user where tenant_id = @Id; delete from catalog.tenant where id = @Id", new { Id = tenantId });
            throw;
        }
        return new Created(tenantId, adminId, dbName);
    }

    /// <summary>Spielt fehlende Migrationen in eine Mandanten-Datenbank ein.</summary>
    public Task<int> MigrateAsync(string databaseName, CancellationToken ct) =>
        new MigrationRunner(databases.ConnectionStringFor(catalog.ConnectionString, databaseName), product.TenantMigrationSets,
            m => log.LogInformation("{Db}: {Migration}", databaseName, m)).MigrateAsync(ct);
}
