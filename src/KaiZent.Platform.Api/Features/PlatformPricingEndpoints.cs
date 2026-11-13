using Dapper;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Contracts.Billing;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace KaiZent.Platform.Api.Features;

/// <summary>
/// Plattform-Administration: Preisstände des Abo-Pakets. Ein neuer Stand gilt ab einem Datum (heute oder später); geplante Stände lassen sich
/// bis zu ihrem Beginn wieder löschen, gültige und frühere bleiben als Verlauf unverändert.
/// </summary>
public static class PlatformPricingEndpoints
{
    public static void MapPlatformPricing(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization(Policies.Platform).AddEndpointFilter<AccessGuard>();
        group.MapGet(PlatformRoutes.PlatformPricing, ListAsync).WithName("ListPackagePrices");
        group.MapPost(PlatformRoutes.PlatformPricing, CreateAsync).WithName("CreatePackagePrice");
        group.MapDelete(PlatformRoutes.PlatformPricing + "/{id:guid}", DeleteAsync).WithName("DeletePackagePrice");
    }

    private const string SelectSql = """
        select id as Id, valid_from as ValidFrom, monthly_net as MonthlyNet, yearly_net as YearlyNet, free_months as FreeMonths,
               reduced_months as ReducedMonths, reduced_monthly_net as ReducedMonthlyNet, note as Note, created_at as CreatedAt, created_by as CreatedBy
        from catalog.package_price
        """;

    private static async Task<PackagePricing> ListAsync(CatalogDb catalog, TimeProvider time, CancellationToken ct)
    {
        var today = PlatformClock.Today(time);
        await using var conn = await catalog.OpenAsync(ct);
        var all = (await conn.QueryAsync<PackagePrice>(new CommandDefinition(SelectSql + " order by valid_from", cancellationToken: ct))).ToList();
        var current = PackagePriceRules.ActiveOn(all, today);
        return new PackagePricing(today, current,
            all.Where(p => p.ValidFrom > today).ToList(),
            all.Where(p => p.ValidFrom < (current?.ValidFrom ?? DateOnly.MinValue)).OrderByDescending(p => p.ValidFrom).ToList());
    }

    private static async Task<IResult> CreateAsync(CreatePackagePriceRequest request, HttpContext http, CatalogDb catalog, TimeProvider time,
        CancellationToken ct)
    {
        new Validator().Add(PackagePriceRules.Check(request, PlatformClock.Today(time))).ThrowIfInvalid();
        await using var conn = await catalog.OpenAsync(ct);
        if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from catalog.package_price where valid_from = @ValidFrom)", new { request.ValidFrom }, cancellationToken: ct)))
            throw ApiProblem.Conflict("Für dieses Datum gibt es schon einen Preis. Bitte den geplanten Preis löschen und neu anlegen.");

        var id = Guid.NewGuid();
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into catalog.package_price (id, valid_from, monthly_net, yearly_net, free_months, reduced_months, reduced_monthly_net, note, created_by)
            values (@Id, @ValidFrom, @MonthlyNet, @YearlyNet, @FreeMonths, @ReducedMonths, @Reduced, @Note, @By)
            """, new
        {
            Id = id, request.ValidFrom, request.MonthlyNet, request.YearlyNet, request.FreeMonths, request.ReducedMonths,
            Reduced = request.ReducedMonths > 0 ? request.ReducedMonthlyNet : null, Note = note, By = http.User.DisplayName(),
        }, tx, cancellationToken: ct));
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "pricing.created", null, new
        {
            request.ValidFrom, request.MonthlyNet, request.YearlyNet, request.FreeMonths, request.ReducedMonths, request.ReducedMonthlyNet,
        }, ct, tx);
        await tx.CommitAsync(ct);
        var created = await conn.QuerySingleAsync<PackagePrice>(new CommandDefinition(SelectSql + " where id = @Id", new { Id = id }, cancellationToken: ct));
        return Results.Created(PlatformRoutes.PlatformPrice(id), created);
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, CatalogDb catalog, TimeProvider time, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var validFrom = await conn.QuerySingleOrDefaultAsync<DateOnly?>(new CommandDefinition(
            "select valid_from from catalog.package_price where id = @Id", new { Id = id }, cancellationToken: ct))
            ?? throw ApiProblem.NotFound("Preis nicht gefunden.");
        // gültige und frühere Stände sind Grundlage von Rechnungen und bleiben unverändert
        if (validFrom <= PlatformClock.Today(time)) throw ApiProblem.Conflict("Nur geplante Preise lassen sich löschen; dieser Preis gilt bereits.");
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("delete from catalog.package_price where id = @Id", new { Id = id }, tx, cancellationToken: ct));
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "pricing.deleted", null, new { ValidFrom = validFrom }, ct, tx);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
