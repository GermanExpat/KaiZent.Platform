namespace KaiZent.Platform.Contracts.Preferences;

/// <summary>Einstellungen eines Benutzers. Werte sind Schlüssel aus <see cref="ThemeCatalog"/>, <see cref="Languages"/> und <see cref="Densities"/>.</summary>
public sealed record UserPreferences(string Theme, string Language, string Density);

/// <summary>Auswahl für die Einstellungsseite.</summary>
public sealed record PreferenceOptions(IReadOnlyList<ThemeOption> Themes, IReadOnlyList<LanguageOption> Languages, IReadOnlyList<string> Densities);

public sealed record LanguageOption(string Code, string NativeName);

public static class Languages
{
    public const string German = "de";
    public const string English = "en";
    public const string Default = German;

    public static readonly IReadOnlyList<LanguageOption> All = [new(German, "Deutsch"), new(English, "English")];

    public static bool IsKnown(string? code) => All.Any(l => l.Code == code);
}

public static class Densities
{
    public const string Normal = "normal";
    public const string Compact = "compact";
    public static readonly IReadOnlyList<string> All = [Normal, Compact];
    public static bool IsKnown(string? value) => value is Normal or Compact;
}
