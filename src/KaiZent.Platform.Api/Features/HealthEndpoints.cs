using Dapper;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace KaiZent.Platform.Api.Features;

public static class HealthEndpoints
{
    public static void MapHealth(this IEndpointRouteBuilder app) =>
        app.MapGet(PlatformRoutes.Health, async (CatalogDb catalog, CancellationToken ct) =>
        {
            await using var conn = await catalog.OpenAsync(ct);
            await conn.ExecuteScalarAsync<int>(new CommandDefinition("select 1", cancellationToken: ct));
            return Results.Ok(new { status = "ok" });
        }).AllowAnonymous().WithName("Health");
}
