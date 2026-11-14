using System.Security.Cryptography;
using Dapper;
using KaiZent.Platform.Contracts.Settings;
using KaiZent.Platform.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace KaiZent.Platform.Api.Settings;

/// <summary>
/// Rückfall für den E-Mail-Versand aus dem Abschnitt <c>Smtp</c> (Umgebungsvariablen <c>Smtp__Host</c> …, im Betrieb <c>deploy/.env</c>).
/// Gilt nur, solange in der Plattform-Administration kein Host gepflegt ist.
/// </summary>
public sealed class PlatformSmtpOptions
{
    public const string Section = "Smtp";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
    public string FromName { get; set; } = "KaiZent";
    public bool UseSsl { get; set; } = true;
}

/// <summary>
/// Rückfall für die KI-Anbindung aus dem Abschnitt <c>Ai</c> (<c>Ai__Provider</c>, <c>Ai__ApiKey</c>, <c>Ai__Model</c>).
/// Gilt nur, solange in der Plattform-Administration kein Anbieter gepflegt ist. <see cref="TimeoutSeconds"/> gilt immer.
/// </summary>
public sealed class PlatformAiOptions
{
    public const string Section = "Ai";

    public string? Provider { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public int TimeoutSeconds { get; set; } = 110;
}

/// <summary>Gerade gültiger E-Mail-Versand (aus der Administration oder dem Rückfall).</summary>
public sealed record EffectiveMail(string Host, int Port, string User, string Password, string From, string FromName, bool UseSsl, string Source)
{
    public bool IsConfigured => Host.Length > 0 && From.Length > 0;
}

/// <summary>Gerade gültige KI-Anbindung (aus der Administration oder dem Rückfall).</summary>
public sealed record EffectiveAi(AiProviderInfo? Provider, string ApiKey, string Model, int TimeoutSeconds, string Source)
{
    /// <summary>Anbieter, wenn auch ein Schlüssel da ist; sonst null (KI abgeschaltet).</summary>
    public AiProviderInfo? Active => Provider is not null && ApiKey.Length > 0 ? Provider : null;

    public string ModelFor(AiProviderInfo provider) => Model.Length == 0 ? provider.DefaultModel : Model;
}

/// <summary>
/// Systemeinstellungen der Plattform (E-Mail-Versand, KI) aus dem Katalog, mit den Umgebungsvariablen als Rückfall. Hält einen Stand im Speicher,
/// den das Speichern in der Administration sofort erneuert; sonst wird er nach <see cref="MaxAge"/> im Hintergrund neu gelesen.
/// Geheimnisse liegen im Katalog nur mit der <see cref="SecretBox"/> verschlüsselt.
/// </summary>
public sealed class PlatformSettings
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private readonly CatalogDb _catalog;
    private readonly SecretBox _secrets;
    private readonly PlatformSmtpOptions _envMail;
    private readonly PlatformAiOptions _envAi;
    private readonly TimeProvider _time;
    private readonly ILogger<PlatformSettings> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot _snapshot;
    private int _refreshing;

    private sealed record Snapshot(EffectiveMail Mail, EffectiveAi Ai, DateTimeOffset LoadedAt);

    internal sealed record MailRow(string Host, int Port, string UserName, string? PasswordProtected, string FromAddress, string FromName, bool UseSsl,
        int Version, DateTimeOffset? UpdatedAt, string UpdatedBy);

    internal sealed record AiRow(string Provider, string? ApiKeyProtected, string Model, int Version, DateTimeOffset? UpdatedAt, string UpdatedBy);

    public PlatformSettings(CatalogDb catalog, SecretBox secrets, PlatformSmtpOptions envMail, PlatformAiOptions envAi, TimeProvider time,
        ILogger<PlatformSettings> log)
    {
        _catalog = catalog;
        _secrets = secrets;
        _envMail = envMail;
        _envAi = envAi;
        _time = time;
        _log = log;
        _snapshot = new Snapshot(EnvMail(), EnvAi(), DateTimeOffset.MinValue);
    }

    /// <summary>Gültiger E-Mail-Versand (Stand im Speicher; ist er alt, wird im Hintergrund nachgeladen).</summary>
    public EffectiveMail Mail => Current().Mail;

    /// <summary>Gültige KI-Anbindung (Stand im Speicher; ist er alt, wird im Hintergrund nachgeladen).</summary>
    public EffectiveAi Ai => Current().Ai;

    public async Task<EffectiveMail> GetMailAsync(CancellationToken ct)
    {
        await RefreshIfStaleAsync(ct);
        return _snapshot.Mail;
    }

    public async Task<EffectiveAi> GetAiAsync(CancellationToken ct)
    {
        await RefreshIfStaleAsync(ct);
        return _snapshot.Ai;
    }

    /// <summary>Liest beide Bereiche neu aus dem Katalog. Ist der Katalog nicht erreichbar, bleibt der alte Stand.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await _catalog.OpenAsync(ct);
            var mail = await LoadMailAsync(conn, ct);
            var ai = await LoadAiAsync(conn, ct);
            _snapshot = new Snapshot(Resolve(mail), Resolve(ai), _time.GetUtcNow());
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException)
        {
            _log.LogWarning(e, "Systemeinstellungen ließen sich nicht lesen; es gilt der bisherige Stand.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private Snapshot Current()
    {
        var s = _snapshot;
        if (_time.GetUtcNow() - s.LoadedAt > MaxAge && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
            _ = Task.Run(async () =>
            {
                try { await RefreshAsync(); }
                catch (Exception e) { _log.LogWarning(e, "Systemeinstellungen ließen sich nicht nachladen."); }
                finally { Interlocked.Exchange(ref _refreshing, 0); }
            });
        return s;
    }

    private async Task RefreshIfStaleAsync(CancellationToken ct)
    {
        if (_time.GetUtcNow() - _snapshot.LoadedAt > MaxAge) await RefreshAsync(ct);
    }

    internal static Task<MailRow> LoadMailAsync(NpgsqlConnection conn, CancellationToken ct, NpgsqlTransaction? tx = null) =>
        conn.QuerySingleAsync<MailRow>(new CommandDefinition("""
            select host, port, user_name, password_protected, from_address, from_name, use_ssl, version, updated_at, updated_by
            from catalog.mail_settings where id = 1
            """, transaction: tx, cancellationToken: ct));

    internal static Task<AiRow> LoadAiAsync(NpgsqlConnection conn, CancellationToken ct, NpgsqlTransaction? tx = null) =>
        conn.QuerySingleAsync<AiRow>(new CommandDefinition(
            "select provider, api_key_protected, model, version, updated_at, updated_by from catalog.ai_settings where id = 1",
            transaction: tx, cancellationToken: ct));

    /// <summary>Entschlüsselt ein Geheimnis; passt der Schlüssel nicht (z. B. Schlüsseldatei verloren), gilt es als nicht hinterlegt.</summary>
    internal string Reveal(string? protectedValue, string what)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            return _secrets.Unprotect(protectedValue);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            _log.LogWarning("{What} lässt sich mit dem Schlüssel der SecretBox nicht entschlüsseln und wird ignoriert.", what);
            return "";
        }
    }

    internal string Protect(string plain) => _secrets.Protect(plain);

    internal EffectiveMail Resolve(MailRow row)
    {
        if (row.Host.Length == 0) return EnvMail();
        return new EffectiveMail(row.Host, row.Port, row.UserName, Reveal(row.PasswordProtected, "Das SMTP-Passwort"), row.FromAddress,
            string.IsNullOrWhiteSpace(row.FromName) ? "KaiZent" : row.FromName, row.UseSsl, SettingSources.Database);
    }

    internal EffectiveAi Resolve(AiRow row)
    {
        if (row.Provider.Length == 0) return EnvAi();
        return new EffectiveAi(AiProviders.Find(row.Provider), Reveal(row.ApiKeyProtected, "Der KI-Schlüssel"), row.Model, Timeout,
            SettingSources.Database);
    }

    private int Timeout => Math.Clamp(_envAi.TimeoutSeconds, 10, 600);

    private EffectiveMail EnvMail()
    {
        var host = _envMail.Host?.Trim() ?? "";
        return new EffectiveMail(host, _envMail.Port, _envMail.User?.Trim() ?? "", _envMail.Password ?? "", _envMail.From?.Trim() ?? "",
            string.IsNullOrWhiteSpace(_envMail.FromName) ? "KaiZent" : _envMail.FromName.Trim(), _envMail.UseSsl,
            host.Length > 0 ? SettingSources.Environment : SettingSources.None);
    }

    private EffectiveAi EnvAi()
    {
        var provider = AiProviders.Find(_envAi.Provider);
        var key = _envAi.ApiKey?.Trim() ?? "";
        return new EffectiveAi(provider, key, _envAi.Model?.Trim() ?? "", Timeout,
            provider is not null && key.Length > 0 ? SettingSources.Environment : SettingSources.None);
    }
}
