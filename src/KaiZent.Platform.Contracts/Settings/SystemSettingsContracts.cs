using KaiZent.Platform.Contracts.Common;

namespace KaiZent.Platform.Contracts.Settings;

/// <summary>Woher die gerade gültigen Einstellungen eines Bereichs stammen.</summary>
public static class SettingSources
{
    /// <summary>In der Plattform-Administration gepflegt (Katalog).</summary>
    public const string Database = "database";
    /// <summary>Rückfall auf Umgebungsvariablen bzw. <c>appsettings</c> (z. B. <c>deploy/.env</c>), solange in der Administration nichts gepflegt ist.</summary>
    public const string Environment = "environment";
    /// <summary>Weder noch: der Bereich ist abgeschaltet.</summary>
    public const string None = "none";
}

/// <summary>KI-Anbieter mit Bildverständnis, die ein Produkt nutzen darf.</summary>
public sealed record AiProviderInfo(string Code, string Name, bool IsEu, string DefaultModel);

public static class AiProviders
{
    public const string Mistral = "mistral";
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";

    /// <summary>Standardmodelle mit Bildverständnis. Mistral: Rechenzentren in der EU, erste Wahl.</summary>
    public static readonly IReadOnlyList<AiProviderInfo> All =
    [
        new(Mistral, "Mistral AI", true, "mistral-medium-latest"),
        new(OpenAi, "OpenAI", false, "gpt-5-mini"),
        new(Anthropic, "Anthropic", false, "claude-sonnet-5-5"),
    ];

    public static AiProviderInfo? Find(string? code) => All.FirstOrDefault(p => p.Code == code?.Trim().ToLowerInvariant());
}

/// <summary>
/// E-Mail-Versand (SMTP) der Plattform und der Produkte. Das Passwort verlässt die API nie: <see cref="PasswordSet"/> sagt nur, ob eines hinterlegt ist.
/// Die Felder zeigen die in der Administration gepflegten Werte; <see cref="Source"/> und <see cref="IsActive"/> beschreiben, was gerade gilt.
/// </summary>
public sealed record MailSettings(string Host, int Port, string User, bool PasswordSet, string From, string FromName, bool UseSsl,
    string Source, bool IsActive, string ActiveHost, string ActiveFrom, int Version, DateTimeOffset? UpdatedAt, string UpdatedBy);

/// <summary>
/// Speichern der SMTP-Einstellungen. <paramref name="Password"/> leer oder null: das hinterlegte bleibt; <paramref name="ClearPassword"/> entfernt es.
/// Ein leerer <paramref name="Host"/> schaltet zurück auf die Umgebungsvariablen.
/// </summary>
public sealed record UpdateMailSettingsRequest(string Host, int Port, string User, string? Password, bool ClearPassword, string From, string FromName,
    bool UseSsl, int Version);

/// <summary>Test-E-Mail mit den gerade gültigen Einstellungen.</summary>
public sealed record SendTestMailRequest(string To);

/// <summary>
/// KI-Anbindung. Der API-Schlüssel verlässt die API nie: <see cref="ApiKeySet"/> und <see cref="ApiKeyHint"/> (letzte vier Zeichen) genügen zum Wiedererkennen.
/// </summary>
public sealed record AiSettings(string Provider, bool ApiKeySet, string ApiKeyHint, string Model, string Source, bool IsActive, string ActiveProvider,
    string ActiveModel, IReadOnlyList<AiProviderInfo> Providers, int Version, DateTimeOffset? UpdatedAt, string UpdatedBy);

/// <summary>
/// Speichern der KI-Anbindung. <paramref name="ApiKey"/> leer oder null: der hinterlegte bleibt; <paramref name="ClearApiKey"/> entfernt ihn.
/// Ein leerer <paramref name="Provider"/> schaltet zurück auf die Umgebungsvariablen. <paramref name="Model"/> leer: Standardmodell des Anbieters.
/// </summary>
public sealed record UpdateAiSettingsRequest(string Provider, string? ApiKey, bool ClearApiKey, string Model, int Version);

/// <summary>Ergebnis einer Testfunktion (Test-E-Mail, KI-Verbindung). Fehler kommen als <see cref="Ok"/> = false mit lesbarer Meldung.</summary>
public sealed record SettingsTestResult(bool Ok, string Message);

public static class SystemSettingsRules
{
    public const int MaxHostLength = 255;
    public const int MaxSecretLength = 1000;
    public const int MaxModelLength = 100;

    public static IEnumerable<(string Field, string Message)> Check(UpdateMailSettingsRequest r, bool passwordStored)
    {
        var host = r.Host?.Trim() ?? "";
        if (host.Length > MaxHostLength || host.Any(char.IsWhiteSpace)) yield return (nameof(r.Host), "Bitte einen gültigen Servernamen angeben.");
        if (r.Port is < 1 or > 65535) yield return (nameof(r.Port), "Der Port liegt zwischen 1 und 65535.");
        if ((r.User?.Length ?? 0) > MaxHostLength) yield return (nameof(r.User), $"Höchstens {MaxHostLength} Zeichen.");
        if ((r.Password?.Length ?? 0) > MaxSecretLength) yield return (nameof(r.Password), $"Höchstens {MaxSecretLength} Zeichen.");
        if ((r.FromName?.Length ?? 0) > Validation.MaxNameLength) yield return (nameof(r.FromName), $"Höchstens {Validation.MaxNameLength} Zeichen.");
        if (host.Length == 0) yield break;
        if (!Validation.IsEmail(r.From?.Trim())) yield return (nameof(r.From), "Bitte eine gültige Absenderadresse angeben.");
        var hasPassword = !string.IsNullOrEmpty(r.Password) || (passwordStored && !r.ClearPassword);
        if (!string.IsNullOrWhiteSpace(r.User) && !hasPassword) yield return (nameof(r.Password), "Bitte das Passwort zum Benutzer angeben.");
    }

    public static IEnumerable<(string Field, string Message)> Check(UpdateAiSettingsRequest r, bool keyStored)
    {
        var provider = r.Provider?.Trim() ?? "";
        if ((r.ApiKey?.Length ?? 0) > MaxSecretLength) yield return (nameof(r.ApiKey), $"Höchstens {MaxSecretLength} Zeichen.");
        if ((r.Model?.Length ?? 0) > MaxModelLength || (r.Model ?? "").Trim().Any(char.IsWhiteSpace))
            yield return (nameof(r.Model), "Bitte einen gültigen Modellnamen angeben.");
        if (provider.Length == 0) yield break;
        if (AiProviders.Find(provider) is null) yield return (nameof(r.Provider), "Unbekannter Anbieter.");
        var hasKey = !string.IsNullOrWhiteSpace(r.ApiKey) || (keyStored && !r.ClearApiKey);
        if (!hasKey) yield return (nameof(r.ApiKey), "Bitte den API-Schlüssel angeben.");
    }

    /// <summary>Letzte vier Zeichen eines Schlüssels zum Wiedererkennen; kurze Schlüssel bleiben ganz verborgen.</summary>
    public static string Hint(string? secret) =>
        string.IsNullOrEmpty(secret) || secret.Length < 12 ? "" : "…" + secret[^4..];
}
