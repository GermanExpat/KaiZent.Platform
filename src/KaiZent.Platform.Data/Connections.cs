using System.Collections.Concurrent;
using Npgsql;

namespace KaiZent.Platform.Data;

/// <summary>
/// Verbindung zur Katalog-Datenbank. Jede angemeldete Anfrage fragt hier kurz nach (AccessGuard); <paramref name="maxPoolSize"/> begrenzt den
/// Pool, damit Katalog und Mandanten-Datenbanken zusammen unter max_connections bleiben (ohne Angabe gilt die Verbindungszeichenfolge).
/// </summary>
public sealed class CatalogDb(string connectionString, int? maxPoolSize = null) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(maxPoolSize is { } max
        ? new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = Math.Max(1, max) }.ConnectionString
        : connectionString);

    public string ConnectionString { get; } = connectionString;

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct = default) => _source.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}

/// <summary>Grenzen für die Verbindungen der API zu den Mandanten-Datenbanken (siehe <see cref="TenantConnections"/>).</summary>
public sealed record TenantPoolLimits
{
    /// <summary>Höchstens so viele Mandanten-Verbindungen sind gleichzeitig in Benutzung, über alle Mandanten; weitere Anfragen warten kurz.</summary>
    public int MaxActive { get; init; } = 60;

    /// <summary>Höchstens so viele Verbindungen hält ein einzelner Mandant, damit ein Mandant unter Last die anderen nicht verdrängt.</summary>
    public int MaxPerTenant { get; init; } = 15;

    /// <summary>Nach so vielen Sekunden ohne Nutzung wird eine Verbindung geschlossen (Npgsql-Standard: 300).</summary>
    public int IdleSeconds { get; init; } = 30;

    /// <summary>So lange wartet eine Anfrage höchstens auf eine freie Verbindung, danach Fehler statt endlosem Warten.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Verbindungen zu den Mandanten-Datenbanken. In der API entscheidet allein der TenantContext anhand des angemeldeten Benutzers, welche
/// Datenbank eine Anfrage nutzt; dort gibt es einen Pool je Datenbank (<see cref="Pooled"/>). Der Worker geht die Mandanten nacheinander
/// durch und arbeitet ohne Pool: sonst bliebe je Mandant eine Verbindung im Leerlauf offen, und bei vielen Mandanten wären die Verbindungen des
/// Servers (max_connections) erschöpft.
/// <para>
/// Ein Pool je Mandant allein reicht bei vielen Mandanten nicht: jeder hält Verbindungen, und alle zusammen überschreiten max_connections
/// ("53300: too many clients"). Deshalb (1) begrenzt <see cref="TenantPoolLimits.MaxActive"/> die gleichzeitig benutzten Verbindungen über
/// alle Mandanten (weitere Anfragen warten, statt zu scheitern), (2) schließt jeder Pool ungenutzte Verbindungen nach
/// <see cref="TenantPoolLimits.IdleSeconds"/>, und (3) räumt eine Anfrage, die trotzdem auf "too many clients" trifft, die ruhenden
/// Verbindungen der anderen Mandanten ab und versucht es erneut.
/// </para>
/// </summary>
public sealed class TenantConnections : IAsyncDisposable
{
    private const string TooManyConnections = "53300";

    private readonly CatalogDb _catalog;
    private readonly TenantDatabases _databases;
    private readonly bool _pooled;
    private readonly TenantPoolLimits _limits;
    private readonly SemaphoreSlim? _active;
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _sources = new();

    private TenantConnections(CatalogDb catalog, TenantDatabases databases, bool pooled, TenantPoolLimits? limits)
    {
        _catalog = catalog;
        _databases = databases;
        _pooled = pooled;
        _limits = limits ?? new TenantPoolLimits();
        if (pooled) _active = new SemaphoreSlim(Math.Max(1, _limits.MaxActive));
    }

    /// <summary>Ohne Pool (Worker, Durchläufe über alle Mandanten, Tests).</summary>
    public TenantConnections(CatalogDb catalog, TenantDatabases databases) : this(catalog, databases, pooled: false, null) { }

    /// <summary>Mit einem Pool je Mandanten-Datenbank und gemeinsamer Obergrenze (API).</summary>
    public static TenantConnections Pooled(CatalogDb catalog, TenantDatabases databases, TenantPoolLimits? limits = null) =>
        new(catalog, databases, pooled: true, limits);

    /// <summary>Gerade benutzte Mandanten-Verbindungen (nur mit Pool), für Tests und Überwachung.</summary>
    public int ActiveCount => _active is null ? 0 : Math.Max(1, _limits.MaxActive) - _active.CurrentCount;

    /// <summary>Verbindung zur Mandanten-Datenbank; mit Pool zählt sie bis zum Schließen gegen <see cref="TenantPoolLimits.MaxActive"/>.</summary>
    public ValueTask<NpgsqlConnection> OpenAsync(string databaseName, CancellationToken ct = default) =>
        _pooled ? OpenPooledAsync(databaseName, limited: true, ct) : OpenUnpooledAsync(databaseName, ct);

    /// <summary>
    /// Zweite Verbindung derselben Anfrage, solange deren erste noch offen ist: zählt nicht gegen die Obergrenze, sonst könnten sich
    /// Anfragen gegenseitig blockieren (alle halten eine Verbindung und warten auf die zweite).
    /// </summary>
    public ValueTask<NpgsqlConnection> OpenNestedAsync(string databaseName, CancellationToken ct = default) =>
        _pooled ? OpenPooledAsync(databaseName, limited: false, ct) : OpenUnpooledAsync(databaseName, ct);

    private async ValueTask<NpgsqlConnection> OpenPooledAsync(string databaseName, bool limited, CancellationToken ct)
    {
        var source = _sources.GetOrAdd(databaseName, CreateSource);
        if (limited && !await _active!.WaitAsync(_limits.WaitTimeout, ct))
            throw new TimeoutException($"Keine freie Datenbankverbindung nach {_limits.WaitTimeout.TotalSeconds:0} s (Obergrenze {_limits.MaxActive}).");
        try
        {
            var conn = await OpenWithRetryAsync(source, ct);
            if (limited)
            {
                var released = 0;
                conn.StateChange += (_, e) =>
                {
                    if (e.CurrentState == System.Data.ConnectionState.Closed && Interlocked.Exchange(ref released, 1) == 0) _active!.Release();
                };
            }
            return conn;
        }
        catch
        {
            if (limited) _active!.Release();
            throw;
        }
    }

    private NpgsqlDataSource CreateSource(string databaseName) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_databases.ConnectionStringFor(_catalog.ConnectionString, databaseName))
        {
            MaxPoolSize = Math.Max(1, _limits.MaxPerTenant),
            MinPoolSize = 0,
            ConnectionIdleLifetime = Math.Max(1, _limits.IdleSeconds),
            ConnectionPruningInterval = Math.Clamp(_limits.IdleSeconds / 3, 1, 10),
        }.ConnectionString);

    /// <summary>Bei "too many clients" die ruhenden Verbindungen der anderen Mandanten schließen und erneut versuchen.</summary>
    private async ValueTask<NpgsqlConnection> OpenWithRetryAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await source.OpenConnectionAsync(ct);
            }
            catch (PostgresException e) when (e.SqlState == TooManyConnections && attempt < 4)
            {
                foreach (var other in _sources.Values) other.Clear();
                await Task.Delay(50 * attempt, ct);
            }
        }
    }

    /// <summary>
    /// Verbindung ohne Pool, für Durchläufe über alle Mandanten (Auswertung, Verbrauchsmessung): sonst bliebe je Mandant eine Verbindung im Leerlauf
    /// offen, und bei vielen Mandanten wären die Verbindungen des Servers schnell erschöpft.
    /// </summary>
    public async ValueTask<NpgsqlConnection> OpenUnpooledAsync(string databaseName, CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(TenantDatabases.Unpooled(_databases.ConnectionStringFor(_catalog.ConnectionString, databaseName)));
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values) await source.DisposeAsync();
        _active?.Dispose();
    }
}
