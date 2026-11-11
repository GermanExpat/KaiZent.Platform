using KaiZent.Platform.Data;

namespace KaiZent.Platform.Api;

/// <summary>
/// Was ein Produkt der Plattform mitgibt: Name, Präfix der Mandanten-Datenbanken, eigene Migrationen je Mandant und die Fachmodule,
/// die die Plattform-Administration einem Mandanten freischalten kann.
/// </summary>
/// <param name="Name">Anzeigename, z. B. "KaiZent LMS".</param>
/// <param name="DatabasePrefix">Präfix der Mandanten-Datenbanken, z. B. <c>lms_farm_</c>.</param>
/// <param name="TenantMigrations">Migrationen des Produkts für jede Mandanten-Datenbank (laufen nach denen der Plattform).</param>
/// <param name="Modules">Erlaubte Fachmodule, z. B. <c>equine</c>, <c>livestock</c>.</param>
public sealed record PlatformProduct(string Name, string DatabasePrefix, MigrationSet? TenantMigrations, IReadOnlyList<string> Modules)
{
    /// <summary>Alle Migrationen einer Mandanten-Datenbank: erst Plattform, dann Produkt.</summary>
    public IReadOnlyList<MigrationSet> TenantMigrationSets =>
        TenantMigrations is null ? [MigrationSet.PlatformTenant] : [MigrationSet.PlatformTenant, TenantMigrations];
}
