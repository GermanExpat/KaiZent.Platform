using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace KaiZent.Platform.Data;

/// <summary>
/// Eine Reihe versionierter SQL-Skripte aus eingebetteten Ressourcen. <see cref="Source"/> trennt die Versionsnummern: die Plattform und das
/// Produkt zählen jeweils ab V0001, ohne sich in die Quere zu kommen.
/// </summary>
/// <param name="Source">Kennung in <c>core.schema_version</c>, z. B. <c>platform</c> oder <c>lms</c>.</param>
/// <param name="Assembly">Assembly mit den eingebetteten Skripten.</param>
/// <param name="ResourcePrefix">Präfix der Ressourcennamen, z. B. <c>migrations/FARM/</c>.</param>
public sealed record MigrationSet(string Source, Assembly Assembly, string ResourcePrefix)
{
    /// <summary>Katalog-Migrationen der Plattform.</summary>
    public static MigrationSet PlatformCatalog { get; } = new("platform", typeof(MigrationSet).Assembly, "kaizent-platform/migrations/CATALOG/");

    /// <summary>Migrationen der Plattform für jede Mandanten-Datenbank (Protokoll).</summary>
    public static MigrationSet PlatformTenant { get; } = new("platform", typeof(MigrationSet).Assembly, "kaizent-platform/migrations/TENANT/");

    public sealed record Migration(int Version, string Name, string Sql, string Checksum);

    /// <summary>Alle Skripte, aufsteigend nach Version. Falsche Namen und doppelte Versionen sind Fehler.</summary>
    public IReadOnlyList<Migration> Load()
    {
        var list = new List<Migration>();
        foreach (var resource in Assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var file = resource[ResourcePrefix.Length..];
            var sep = file.IndexOf("__", StringComparison.Ordinal);
            if (!file.StartsWith('V') || sep < 2 || !file.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) || !int.TryParse(file[1..sep], out var version))
                throw new InvalidOperationException($"Ungültiger Migrationsname '{resource}', erwartet V####__name.sql.");
            using var stream = Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd();
            list.Add(new Migration(version, file[(sep + 2)..^4], sql, Hash(sql)));
        }
        var dupes = list.GroupBy(m => m.Version).Where(g => g.Count() > 1).Select(g => $"V{g.Key:0000}").ToList();
        if (dupes.Count > 0) throw new InvalidOperationException($"Doppelte Migrationsversionen in {Source} ({ResourcePrefix}): {string.Join(", ", dupes)}");
        return list.OrderBy(m => m.Version).ToList();
    }

    private static string Hash(string sql) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n"))));
}

/// <summary>
/// Spielt fehlende Migrationen ein. Eingespielte Skripte stehen mit Prüfsumme in <c>core.schema_version</c> (je Quelle); wird ein bereits
/// eingespieltes Skript geändert, bricht der Lauf ab. Migrationen werden also nur angehängt, nie geändert. Jedes Skript läuft in einer
/// eigenen Transaktion; die Reihen laufen in der angegebenen Reihenfolge (Plattform vor Produkt).
/// </summary>
public sealed class MigrationRunner(string connectionString, IReadOnlyList<MigrationSet> sets, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    /// <summary>Spielt alle fehlenden Migrationen ein und liefert ihre Anzahl.</summary>
    public async Task<int> MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(TenantDatabases.Unpooled(connectionString));
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            create schema if not exists core;
            create table if not exists core.schema_version (
                source       text        not null,
                version      integer     not null,
                name         text        not null,
                checksum     text        not null,
                applied_at   timestamptz not null default now(),
                duration_ms  integer     not null,
                primary key (source, version)
            );
            """, cancellationToken: ct));

        var count = 0;
        foreach (var set in sets)
        {
            var applied = (await conn.QueryAsync<(int Version, string Checksum)>(new CommandDefinition(
                    "select version, checksum from core.schema_version where source = @Source", new { set.Source }, cancellationToken: ct)))
                .ToDictionary(x => x.Version, x => x.Checksum);
            foreach (var m in set.Load())
            {
                if (applied.TryGetValue(m.Version, out var checksum))
                {
                    if (checksum != m.Checksum)
                        throw new InvalidOperationException(
                            $"Migration {set.Source}/V{m.Version:0000} '{m.Name}' wurde nach dem Einspielen geändert. Bitte eine neue Migration anlegen.");
                    continue;
                }
                var started = DateTime.UtcNow;
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(new CommandDefinition(m.Sql, transaction: tx, commandTimeout: 600, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "insert into core.schema_version(source, version, name, checksum, duration_ms) values (@Source, @Version, @Name, @Checksum, @Ms)",
                    new { set.Source, m.Version, m.Name, m.Checksum, Ms = (int)(DateTime.UtcNow - started).TotalMilliseconds }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                _log($"{set.Source}/V{m.Version:0000} {m.Name} eingespielt");
                count++;
            }
        }
        return count;
    }
}
