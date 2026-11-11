namespace KaiZent.Platform.Contracts.Preferences;

/// <summary>Familie eines Farbschemas: Fluent (Akzentfarbe, hell oder dunkel) oder eines der klassischen DevExpress-Themes.</summary>
public enum ThemeFamily { Fluent, Classic }

/// <summary>
/// Ein wählbares Farbschema. <see cref="Accent"/> ist bei Fluent der Name aus <c>DevExpress.Blazor.ThemeFluentAccentColor</c>,
/// bei Classic der Feldname aus <c>DevExpress.Blazor.Themes</c>. <see cref="PreviewColor"/> ist die Akzentfarbe für die Vorschau im Menü.
/// </summary>
public sealed record ThemeOption(string Key, ThemeFamily Family, string Accent, bool IsDark, string NameDe, string NameEn, string PreviewColor);

/// <summary>
/// Die DevExpress-Standardschemata, wie im Theme-Umschalter der DevExpress-Demos: Fluent mit den elf Akzentfarben, jeweils hell und dunkel,
/// dazu Blazing Berry, Blazing Dark, Office White und Purple. Gespeichert wird nur der <see cref="ThemeOption.Key"/>; was nicht in dieser
/// Liste steht, wird nie angenommen.
/// </summary>
public static class ThemeCatalog
{
    public const string Default = "fluent-light-blue";

    private static readonly (string Accent, string De, string En, string Hex)[] FluentAccents =
    [
        ("Blue", "Blau", "Blue", "#0f6cbd"),
        ("CoolBlue", "Kühles Blau", "Cool Blue", "#2d7d9a"),
        ("Desert", "Wüste", "Desert", "#847545"),
        ("Mint", "Minze", "Mint", "#018574"),
        ("Moss", "Moos", "Moss", "#486860"),
        ("Orchid", "Orchidee", "Orchid", "#c239b3"),
        ("Purple", "Violett", "Purple", "#5b5fc7"),
        ("Rose", "Rose", "Rose", "#ea005e"),
        ("Rust", "Rost", "Rust", "#da3b01"),
        ("Steel", "Stahl", "Steel", "#68768a"),
        ("Storm", "Sturm", "Storm", "#4c4a48"),
    ];

    public static readonly IReadOnlyList<ThemeOption> All = Build();

    private static List<ThemeOption> Build()
    {
        var list = new List<ThemeOption>();
        foreach (var dark in new[] { false, true })
            foreach (var (accent, de, en, hex) in FluentAccents)
                list.Add(new ThemeOption(
                    $"fluent-{(dark ? "dark" : "light")}-{accent.ToLowerInvariant()}", ThemeFamily.Fluent, accent, dark,
                    $"Fluent {(dark ? "Dunkel" : "Hell")} {de}", $"Fluent {(dark ? "Dark" : "Light")} {en}", hex));
        list.Add(new("classic-blazing-berry", ThemeFamily.Classic, "BlazingBerry", false, "Blazing Berry", "Blazing Berry", "#5c2d91"));
        list.Add(new("classic-blazing-dark", ThemeFamily.Classic, "BlazingDark", true, "Blazing Dark", "Blazing Dark", "#46444a"));
        list.Add(new("classic-office-white", ThemeFamily.Classic, "OfficeWhite", false, "Office White", "Office White", "#fe7109"));
        list.Add(new("classic-purple", ThemeFamily.Classic, "Purple", false, "Purple", "Purple", "#7b3980"));
        return list;
    }

    public static ThemeOption? Find(string? key) => All.FirstOrDefault(t => t.Key == key);

    public static bool IsKnown(string? key) => Find(key) is not null;

    /// <summary>Unbekannte oder manipulierte Werte (z. B. aus einem Cookie) ergeben das Standardschema.</summary>
    public static ThemeOption Resolve(string? key) => Find(key) ?? Find(Default)!;
}
