using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace KaiZent.Platform.Data;

/// <summary>
/// Anlage und Adressierung der Mandanten-Datenbanken. Jeder Mandant hat eine eigene PostgreSQL-Datenbank "&lt;Präfix&gt;&lt;id&gt;"
/// (LMS: <c>lms_farm_…</c>) auf demselben Server wie der Katalog; die Verbindung nutzt dieselben Zugangsdaten, nur der Datenbankname wechselt.
/// </summary>
public sealed partial class TenantDatabases
{
    [GeneratedRegex("^[a-z][a-z0-9_]{1,30}_$")]
    private static partial Regex PrefixPattern();

    private readonly Regex _namePattern;

    /// <param name="prefix">Präfix des Produkts, klein, endet auf "_", z. B. <c>lms_farm_</c>.</param>
    public TenantDatabases(string prefix)
    {
        if (!PrefixPattern().IsMatch(prefix)) throw new ArgumentException($"Ungültiges Präfix für Mandanten-Datenbanken: '{prefix}'.", nameof(prefix));
        Prefix = prefix;
        _namePattern = new Regex("^" + Regex.Escape(prefix) + "[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    }

    public string Prefix { get; }

    public string NameFor(Guid tenantId) => Prefix + tenantId.ToString("N");

    public bool IsValidName(string name) => _namePattern.IsMatch(name);

    /// <summary>
    /// Dieselbe Verbindung ohne Pool, für einmalige Arbeiten (Migrationen, Anlage, Löschen). Mit Pool bliebe danach je Datenbank eine
    /// Verbindung minutenlang im Leerlauf offen; beim Start mit vielen Mandanten wären so die Verbindungen des Servers aufgebraucht.
    /// </summary>
    public static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    /// <summary>Verbindungszeichenfolge der Mandanten-Datenbank aus der des Katalogs.</summary>
    public string ConnectionStringFor(string catalogConnectionString, string databaseName)
    {
        EnsureValid(databaseName);
        return new NpgsqlConnectionStringBuilder(catalogConnectionString) { Database = databaseName }.ConnectionString;
    }

    /// <summary>Legt die Datenbank an. Der Name wird vorher streng geprüft, weil CREATE DATABASE keine Parameter kennt.</summary>
    public async Task CreateAsync(string catalogConnectionString, string databaseName, CancellationToken ct = default)
    {
        EnsureValid(databaseName);
        await using var conn = new NpgsqlConnection(Unpooled(catalogConnectionString));
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition($"create database \"{databaseName}\"", cancellationToken: ct));
    }

    /// <summary>Entfernt die Datenbank (fehlgeschlagene Anlage, Löschung nach Kündigung).</summary>
    public async Task DropAsync(string catalogConnectionString, string databaseName, CancellationToken ct = default)
    {
        EnsureValid(databaseName);
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(Unpooled(catalogConnectionString));
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition($"drop database if exists \"{databaseName}\" with (force)", cancellationToken: ct));
    }

    private void EnsureValid(string databaseName)
    {
        if (!IsValidName(databaseName)) throw new ArgumentException($"Ungültiger Name einer Mandanten-Datenbank: '{databaseName}'.", nameof(databaseName));
    }
}
