using Dapper;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Security;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace KaiZent.Platform.Api.Infrastructure;

/// <summary>
/// Prüft bei jeder angemeldeten Anfrage im Katalog, ob Konto und Mandant noch aktiv sind und das Token noch gilt (nicht abgemeldet,
/// Sitzungsstand und Rolle aktuell). Eine Deaktivierung oder ein Passwortwechsel wirkt so sofort und nicht erst, wenn das Token abläuft.
/// Muss das Passwort geändert werden, sind nur Passwortänderung, Abmelden und "Ich" erlaubt. Merkt sich die Mandanten-Datenbank für
/// <see cref="TenantContext"/>; die Plattform-Administration bekommt nie eine.
/// </summary>
public sealed class AccessGuard(CatalogDb catalog) : IEndpointFilter
{
    internal const string TenantDbItem = "kz.tenant_db";
    internal const string TenantNameItem = "kz.tenant_name";
    internal const string TenantModulesItem = "kz.tenant_modules";

    private static readonly HashSet<string> AllowedWhilePasswordChangeRequired =
        new([PlatformRoutes.Me, PlatformRoutes.ChangePassword, PlatformRoutes.Logout, PlatformRoutes.MyPreferences, PlatformRoutes.PreferenceOptions],
            StringComparer.OrdinalIgnoreCase);

    private sealed record Row(bool UserActive, string Role, Guid? TenantId, string? DatabaseName, string? TenantName, bool? TenantActive,
        string[]? Modules, int SessionVersion, bool MustChangePassword, bool Revoked);

    private const string Sql = """
        select u.is_active as UserActive, u.role as Role, u.tenant_id as TenantId,
               t.database_name as DatabaseName, t.name as TenantName, t.is_active as TenantActive, t.modules as Modules,
               u.session_version as SessionVersion, u.must_change_password as MustChangePassword,
               exists (select 1 from catalog.revoked_token r where r.jti = @TokenId) as Revoked
        from catalog.app_user u
        left join catalog.tenant t on t.id = u.tenant_id
        where u.id = @UserId
        """;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated != true) return await next(context);

        await using var conn = await catalog.OpenAsync(http.RequestAborted);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(Sql,
            new { UserId = http.User.UserId(), TokenId = http.User.TokenId() ?? "" }, cancellationToken: http.RequestAborted));
        if (row is null || !row.UserActive || row.Revoked || row.SessionVersion != http.User.SessionVersion()
            || row.Role != http.User.Role() || row.TenantId != http.User.TenantId())
            return Results.Json(new ApiError(ErrorCodes.Unauthorized, "Die Anmeldung ist nicht mehr gültig."), statusCode: StatusCodes.Status401Unauthorized);
        if (row.TenantId is not null && row.TenantActive != true)
            return Results.Json(new ApiError(ErrorCodes.TenantInactive, "Der Zugang dieses Mandanten ist deaktiviert."), statusCode: StatusCodes.Status403Forbidden);
        var path = http.Request.Path.Value ?? "";
        if (row.MustChangePassword && !AllowedWhilePasswordChangeRequired.Contains(path))
            return Results.Json(new ApiError(ErrorCodes.PasswordChangeRequired, "Bitte zuerst das Passwort ändern."), statusCode: StatusCodes.Status403Forbidden);

        if (row.TenantId is not null)
        {
            http.Items[TenantDbItem] = row.DatabaseName;
            http.Items[TenantNameItem] = row.TenantName;
            http.Items[TenantModulesItem] = (IReadOnlyList<string>)(row.Modules ?? []);
        }
        return await next(context);
    }
}

/// <summary>
/// Die Datenbank des Mandanten, zu dem der angemeldete Benutzer gehört. Fachendpunkte eines Produkts öffnen Mandanten-Daten nur hierüber;
/// ohne geprüfte Anmeldung (AccessGuard lief nicht) oder für die Plattform-Administration gibt es keine Verbindung.
/// </summary>
public sealed class TenantContext(IHttpContextAccessor accessor, TenantConnections connections)
{
    private HttpContext Http => accessor.HttpContext ?? throw new InvalidOperationException("Kein HTTP-Kontext.");

    public Guid TenantId => Http.User.TenantId() ?? throw ApiProblem.Forbidden("Diese Funktion gehört zu einem Mandanten.");

    public string TenantName => Http.Items[AccessGuard.TenantNameItem] as string ?? "";

    public Guid UserId => Http.User.UserId();

    public string UserName => Http.User.DisplayName();

    public bool IsTenantAdmin => Http.User.Role() == Roles.TenantAdmin;

    /// <summary>Freigeschaltete Fachmodule des Mandanten.</summary>
    public IReadOnlyList<string> Modules => Http.Items[AccessGuard.TenantModulesItem] as IReadOnlyList<string> ?? [];

    public bool HasModule(string module) => Modules.Contains(module, StringComparer.Ordinal);

    /// <summary>Wirft 403, wenn das Modul für diesen Mandanten nicht freigeschaltet ist.</summary>
    public void RequireModule(string module)
    {
        if (!HasModule(module)) throw ApiProblem.Forbidden("Dieses Modul ist für den Mandanten nicht freigeschaltet.");
    }

    private int _open;

    /// <summary>
    /// Verbindung zur Mandanten-Datenbank. Die erste offene Verbindung einer Anfrage zählt gegen die gemeinsame Obergrenze, weitere
    /// gleichzeitig offene nicht (<see cref="TenantConnections.OpenNestedAsync"/>).
    /// </summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (Http.Items[AccessGuard.TenantDbItem] is not string db) throw ApiProblem.Forbidden("Diese Funktion gehört zu einem Mandanten.");
        var conn = Volatile.Read(ref _open) == 0 ? await connections.OpenAsync(db, ct) : await connections.OpenNestedAsync(db, ct);
        Interlocked.Increment(ref _open);
        var closed = 0;
        conn.StateChange += (_, e) =>
        {
            if (e.CurrentState == System.Data.ConnectionState.Closed && Interlocked.Exchange(ref closed, 1) == 0) Interlocked.Decrement(ref _open);
        };
        return conn;
    }

    /// <summary>Protokolleintrag im Mandanten mit dem angemeldeten Benutzer.</summary>
    public Task AuditAsync(NpgsqlConnection db, string action, string entity, string? entityId, object? details = null,
        NpgsqlTransaction? tx = null, CancellationToken ct = default) =>
        AuditLog.WriteAsync(db, UserId, UserName, action, entity, entityId, details, tx, ct);
}

public static class Policies
{
    public const string Platform = "kz-platform";
    public const string Tenant = "kz-tenant";
    public const string TenantAdmin = "kz-tenant-admin";

    public static AuthorizationBuilder AddPlatformPolicies(this AuthorizationBuilder auth) => auth
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
        .AddPolicy(Platform, p => p.RequireRole(Roles.PlatformAdmin))
        .AddPolicy(Tenant, p => p.RequireClaim(PlatformClaims.TenantId).RequireRole(Roles.TenantAdmin, Roles.Member))
        .AddPolicy(TenantAdmin, p => p.RequireClaim(PlatformClaims.TenantId).RequireRole(Roles.TenantAdmin));
}
