using System.Globalization;
using KaiZent.Platform.Contracts.Preferences;

namespace KaiZent.Platform.Web.Services;

/// <summary>Texte eines Produkts oder der Plattform: Schlüssel → (Deutsch, Englisch).</summary>
public sealed class TextCatalog(IReadOnlyDictionary<string, (string De, string En)> entries)
{
    public IReadOnlyDictionary<string, (string De, string En)> Entries { get; } = entries;
}

/// <summary>
/// Übersetzte Texte für die aktuelle Sprache (<see cref="CultureInfo.CurrentUICulture"/>, gesetzt aus den Einstellungen des Kontos,
/// sonst aus dem Sprach-Cookie oder dem Browser). Fehlt ein Schlüssel, erscheint der Schlüssel selbst, damit Lücken auffallen.
/// </summary>
public sealed class Texts
{
    private readonly Dictionary<string, (string De, string En)> _all = new(StringComparer.Ordinal);

    public Texts(IEnumerable<TextCatalog> catalogs)
    {
        foreach (var c in catalogs)
            foreach (var (key, value) in c.Entries) _all[key] = value;   // Produkte dürfen Plattform-Texte überschreiben
    }

    public static bool IsEnglish => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Languages.English;

    public string this[string key] => _all.TryGetValue(key, out var t) ? (IsEnglish ? t.En : t.De) : key;

    public string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, this[key], args);

    public bool Has(string key) => _all.ContainsKey(key);

    /// <summary>Meldung zu einem Fehlercode der API (<c>error.&lt;code&gt;</c>), sonst der deutsche Klartext der API.</summary>
    public string Error(ApiException e) => Has("error." + e.Error.Code) ? this["error." + e.Error.Code] : e.Error.Message;

    /// <summary>Name eines Farbschemas in der aktuellen Sprache.</summary>
    public static string ThemeName(ThemeOption t) => IsEnglish ? t.NameEn : t.NameDe;
}
