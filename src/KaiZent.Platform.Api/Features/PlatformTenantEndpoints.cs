using Dapper;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Tenants;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace KaiZent.Platform.Api.Features;

/// <summary>
/// Plattform-Administration: Mandanten anlegen, ändern, sperren. Arbeitet nur im Katalog; eine Mandanten-Datenbank öffnet sie nie
/// (außer bei der Anlage, um sie zu migrieren).
/// </summary>
public static class PlatformTenantEndpoints
{
    public static void MapPlatformTenants(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization(Policies.Platform).AddEndpointFilter<AccessGuard>();
        group.MapGet(PlatformRoutes.PlatformTenants, ListAsync).WithName("ListTenants");
        group.MapPost(PlatformRoutes.PlatformTenants, CreateAsync).WithName("CreateTenant");
        group.MapGet(PlatformRoutes.PlatformTenants + "/{id:guid}", GetAsync).WithName("GetTenant");
        group.MapPut(PlatformRoutes.PlatformTenants + "/{id:guid}", UpdateAsync).WithName("UpdateTenant");
        group.MapPut(PlatformRoutes.PlatformTenants + "/{id:guid}/status", SetStatusAsync).WithName("SetTenantStatus");
    }

    private const string SummarySql = """
        select t.id as Id, t.name as Name, t.is_active as IsActive, t.modules as Modules, t.contact_email as ContactEmail,
               (select count(*) from catalog.app_user u where u.tenant_id = t.id and u.is_active)::int as UserCount, t.created_at as CreatedAt
        from catalog.tenant t
        """;

    private sealed record Row(Guid Id, string Name, bool IsActive, string[] Modules, string? ContactEmail, int UserCount, DateTimeOffset CreatedAt)
    {
        public TenantSummary ToSummary() => new(Id, Name, IsActive, Modules, ContactEmail, UserCount, CreatedAt);
    }

    private static async Task<IReadOnlyList<TenantSummary>> ListAsync(CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        return (await conn.QueryAsync<Row>(new CommandDefinition(SummarySql + " order by lower(t.name)", cancellationToken: ct))).Select(r => r.ToSummary()).ToList();
    }

    private static async Task<TenantSummary> GetAsync(Guid id, CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        return await LoadAsync(conn, id, ct);
    }

    private static async Task<TenantSummary> LoadAsync(Npgsql.NpgsqlConnection conn, Guid id, CancellationToken ct) =>
        (await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(SummarySql + " where t.id = @Id", new { Id = id }, cancellationToken: ct)))?.ToSummary()
        ?? throw ApiProblem.NotFound("Mandant nicht gefunden.");

    private static void ValidateModules(Validator v, IReadOnlyList<string>? modules, PlatformProduct product) =>
        v.Check(modules is not null && modules.All(m => product.Modules.Contains(m, StringComparer.Ordinal)) && modules.Distinct().Count() == modules.Count,
            "Modules", $"Unbekanntes Modul. Erlaubt: {string.Join(", ", product.Modules)}.");

    private static async Task<IResult> CreateAsync(CreateTenantRequest request, HttpContext http, CatalogDb catalog, TenantProvisioner provisioner,
        PlatformProduct product, IOptions<PlatformOptions> options, CancellationToken ct)
    {
        var adminEmail = request.AdminEmail?.Trim() ?? "";
        var v = new Validator()
            .Check(Validation.IsName(request.Name), nameof(request.Name), "Bitte einen Namen angeben (höchstens 200 Zeichen).")
            .Check(Validation.IsEmail(adminEmail), nameof(request.AdminEmail), "Bitte eine gültige E-Mail-Adresse angeben.")
            .Check(Validation.IsName(request.AdminDisplayName), nameof(request.AdminDisplayName), "Bitte den Namen der Administration angeben.")
            .Check(Validation.IsStrongPassword(request.AdminInitialPassword), nameof(request.AdminInitialPassword), Validation.PasswordRule)
            .Check(request.ContactEmail is null || Validation.IsEmail(request.ContactEmail), nameof(request.ContactEmail), "Die Kontaktadresse ist ungültig.");
        ValidateModules(v, request.Modules, product);
        v.ThrowIfInvalid();

        var created = await provisioner.CreateAsync(request.Name, request.Modules, request.ContactEmail, adminEmail, request.AdminDisplayName,
            Passwords.Hash(request.AdminInitialPassword), options.Value.RequireInitialPasswordChange, ct);
        await using var conn = await catalog.OpenAsync(ct);
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "tenant.created", created.TenantId,
            new { request.Modules }, ct);
        var summary = await LoadAsync(conn, created.TenantId, ct);
        return Results.Created(PlatformRoutes.PlatformTenant(created.TenantId), new CreateTenantResponse(summary, created.AdminUserId));
    }

    private static async Task<TenantSummary> UpdateAsync(Guid id, UpdateTenantRequest request, HttpContext http, CatalogDb catalog, PlatformProduct product,
        CancellationToken ct)
    {
        var v = new Validator()
            .Check(Validation.IsName(request.Name), nameof(request.Name), "Bitte einen Namen angeben (höchstens 200 Zeichen).")
            .Check(request.ContactEmail is null || Validation.IsEmail(request.ContactEmail), nameof(request.ContactEmail), "Die Kontaktadresse ist ungültig.");
        ValidateModules(v, request.Modules, product);
        v.ThrowIfInvalid();
        await using var conn = await catalog.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "update catalog.tenant set name = @Name, modules = @Modules, contact_email = @Contact where id = @Id",
            new { Id = id, Name = request.Name.Trim(), Modules = request.Modules.ToArray(), Contact = request.ContactEmail }, cancellationToken: ct));
        if (n == 0) throw ApiProblem.NotFound("Mandant nicht gefunden.");
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "tenant.updated", id, new { request.Modules }, ct);
        return await LoadAsync(conn, id, ct);
    }

    /// <summary>Sperren wirkt sofort: der AccessGuard lässt Konten eines gesperrten Mandanten bei der nächsten Anfrage nicht mehr durch.</summary>
    private static async Task<TenantSummary> SetStatusAsync(Guid id, SetActiveRequest request, HttpContext http, CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "update catalog.tenant set is_active = @Active, deactivated_at = case when @Active then null else now() end where id = @Id",
            new { Id = id, Active = request.IsActive }, cancellationToken: ct));
        if (n == 0) throw ApiProblem.NotFound("Mandant nicht gefunden.");
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), request.IsActive ? "tenant.activated" : "tenant.deactivated", id, null, ct);
        return await LoadAsync(conn, id, ct);
    }
}
