using Dapper;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Preferences;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace KaiZent.Platform.Api.Features;

/// <summary>Eigene Einstellungen: Farbschema aus den DevExpress-Standardschemata, Sprache (Deutsch, Englisch), Dichte. Gilt für jedes Gerät.</summary>
public static class PreferenceEndpoints
{
    public static void MapPreferences(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization().AddEndpointFilter<AccessGuard>();
        group.MapGet(PlatformRoutes.PreferenceOptions, () => new PreferenceOptions(ThemeCatalog.All, Languages.All, Densities.All)).WithName("GetPreferenceOptions");
        group.MapGet(PlatformRoutes.MyPreferences, GetAsync).WithName("GetMyPreferences");
        group.MapPut(PlatformRoutes.MyPreferences, SaveAsync).WithName("SaveMyPreferences");
    }

    private static async Task<UserPreferences> GetAsync(HttpContext http, CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        return (await AuthEndpoints.CurrentAccountAsync(conn, http, ct)).Preferences;
    }

    private static async Task<UserPreferences> SaveAsync(UserPreferences request, HttpContext http, CatalogDb catalog, CancellationToken ct)
    {
        new Validator()
            .Check(ThemeCatalog.IsKnown(request.Theme), nameof(request.Theme), "Unbekanntes Farbschema.")
            .Check(Languages.IsKnown(request.Language), nameof(request.Language), "Unbekannte Sprache.")
            .Check(Densities.IsKnown(request.Density), nameof(request.Density), "Unbekannte Dichte.")
            .ThrowIfInvalid();
        await using var conn = await catalog.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into catalog.user_preferences (user_id, theme, language, density, updated_at) values (@Id, @Theme, @Language, @Density, now())
            on conflict (user_id) do update set theme = excluded.theme, language = excluded.language, density = excluded.density, updated_at = now()
            """, new { Id = http.User.UserId(), request.Theme, request.Language, request.Density }, cancellationToken: ct));
        return request;
    }
}
