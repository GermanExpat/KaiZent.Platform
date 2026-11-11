using Dapper;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Preferences;
using KaiZent.Platform.Contracts.Security;
using KaiZent.Platform.Contracts.Tenants;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KaiZent.Platform.Api.Features;

/// <summary>
/// Mandanten-Administration: Benutzer des eigenen Mandanten und Einstellungen für alle (Standard-Farbschema und -Sprache).
/// Jede Änderung steht im Protokoll des Mandanten.
/// </summary>
public static class TenantAdminEndpoints
{
    public static void MapTenantAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization(Policies.TenantAdmin).AddEndpointFilter<AccessGuard>();
        group.MapGet(PlatformRoutes.TenantUsers, ListUsersAsync).WithName("ListTenantUsers");
        group.MapPost(PlatformRoutes.TenantUsers, CreateUserAsync).WithName("CreateTenantUser");
        group.MapPut(PlatformRoutes.TenantUsers + "/{id:guid}", UpdateUserAsync).WithName("UpdateTenantUser");
        group.MapPut(PlatformRoutes.TenantUsers + "/{id:guid}/status", SetUserStatusAsync).WithName("SetTenantUserStatus");
        group.MapPut(PlatformRoutes.TenantUsers + "/{id:guid}/password", ResetPasswordAsync).WithName("ResetTenantUserPassword");

        // Standard für neue Benutzer lesen dürfen alle des Mandanten, ändern nur die Administration
        app.MapGet(PlatformRoutes.TenantSettings, GetSettingsAsync).RequireAuthorization(Policies.Tenant).AddEndpointFilter<AccessGuard>().WithName("GetTenantSettings");
        group.MapPut(PlatformRoutes.TenantSettings, SaveSettingsAsync).WithName("SaveTenantSettings");
    }

    private const string UserSql = """
        select id as Id, email as Email, display_name as DisplayName, role as Role, is_active as IsActive, last_login_at as LastLoginAt,
               totp_enabled as TwoFactorEnabled
        from catalog.app_user
        """;

    private static async Task<IReadOnlyList<TenantUser>> ListUsersAsync(TenantContext tenant, CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        return (await conn.QueryAsync<TenantUser>(new CommandDefinition(UserSql + " where tenant_id = @Tenant order by lower(display_name)",
            new { Tenant = tenant.TenantId }, cancellationToken: ct))).ToList();
    }

    private static async Task<TenantUser> LoadUserAsync(NpgsqlConnection conn, Guid tenantId, Guid id, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<TenantUser>(new CommandDefinition(UserSql + " where tenant_id = @Tenant and id = @Id",
            new { Tenant = tenantId, Id = id }, cancellationToken: ct)) ?? throw ApiProblem.NotFound("Benutzer nicht gefunden.");

    private static async Task<IResult> CreateUserAsync(CreateTenantUserRequest request, TenantContext tenant, CatalogDb catalog,
        IOptions<PlatformOptions> options, CancellationToken ct)
    {
        var email = request.Email?.Trim() ?? "";
        new Validator()
            .Check(Validation.IsEmail(email), nameof(request.Email), "Bitte eine gültige E-Mail-Adresse angeben.")
            .Check(Validation.IsName(request.DisplayName), nameof(request.DisplayName), "Bitte einen Namen angeben.")
            .Check(Roles.IsTenantRole(request.Role), nameof(request.Role), "Unbekannte Rolle.")
            .Check(Validation.IsStrongPassword(request.InitialPassword), nameof(request.InitialPassword), Validation.PasswordRule)
            .ThrowIfInvalid();
        var id = Guid.CreateVersion7();
        await using var conn = await catalog.OpenAsync(ct);
        var settings = await LoadSettingsAsync(conn, tenant.TenantId, ct);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                insert into catalog.app_user (id, tenant_id, email, display_name, password_hash, role, must_change_password)
                values (@Id, @Tenant, @Email, @Name, @Hash, @Role, @MustChange);
                insert into catalog.user_preferences (user_id, theme, language) values (@Id, @Theme, @Language);
                """, new { Id = id, Tenant = tenant.TenantId, Email = email, Name = request.DisplayName.Trim(),
                    Hash = Passwords.Hash(request.InitialPassword), request.Role, MustChange = options.Value.RequireInitialPasswordChange,
                    Theme = settings.DefaultTheme, Language = settings.DefaultLanguage }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        await using (var db = await tenant.OpenAsync(ct))
            await tenant.AuditAsync(db, "user.created", "user", id.ToString(), new { request.Role }, ct: ct);
        return Results.Created(PlatformRoutes.TenantUser(id), await LoadUserAsync(conn, tenant.TenantId, id, ct));
    }

    private static async Task<TenantUser> UpdateUserAsync(Guid id, UpdateTenantUserRequest request, TenantContext tenant, CatalogDb catalog, CancellationToken ct)
    {
        new Validator()
            .Check(Validation.IsName(request.DisplayName), nameof(request.DisplayName), "Bitte einen Namen angeben.")
            .Check(Roles.IsTenantRole(request.Role), nameof(request.Role), "Unbekannte Rolle.")
            .Check(id != tenant.UserId || request.Role == Roles.TenantAdmin, nameof(request.Role), "Die eigene Administratorrolle kann man sich nicht selbst nehmen.")
            .ThrowIfInvalid();
        await using var conn = await catalog.OpenAsync(ct);
        // Rollenwechsel erhöht den Sitzungsstand: das Token mit der alten Rolle gilt nicht mehr
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            update catalog.app_user set display_name = @Name, role = @Role,
                   session_version = session_version + case when role <> @Role then 1 else 0 end
            where id = @Id and tenant_id = @Tenant
            """, new { Id = id, Tenant = tenant.TenantId, Name = request.DisplayName.Trim(), request.Role }, cancellationToken: ct));
        if (n == 0) throw ApiProblem.NotFound("Benutzer nicht gefunden.");
        await using (var db = await tenant.OpenAsync(ct))
            await tenant.AuditAsync(db, "user.updated", "user", id.ToString(), new { request.Role }, ct: ct);
        return await LoadUserAsync(conn, tenant.TenantId, id, ct);
    }

    private static async Task<TenantUser> SetUserStatusAsync(Guid id, SetActiveRequest request, TenantContext tenant, CatalogDb catalog, CancellationToken ct)
    {
        if (id == tenant.UserId && !request.IsActive) throw ApiProblem.Conflict("Das eigene Konto kann man nicht deaktivieren.");
        await using var conn = await catalog.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            update catalog.app_user set is_active = @Active, session_version = session_version + case when @Active then 0 else 1 end
            where id = @Id and tenant_id = @Tenant
            """, new { Id = id, Tenant = tenant.TenantId, Active = request.IsActive }, cancellationToken: ct));
        if (n == 0) throw ApiProblem.NotFound("Benutzer nicht gefunden.");
        await using (var db = await tenant.OpenAsync(ct))
            await tenant.AuditAsync(db, request.IsActive ? "user.activated" : "user.deactivated", "user", id.ToString(), ct: ct);
        return await LoadUserAsync(conn, tenant.TenantId, id, ct);
    }

    private static async Task<IResult> ResetPasswordAsync(Guid id, ResetPasswordRequest request, TenantContext tenant, CatalogDb catalog, CancellationToken ct)
    {
        new Validator().Check(Validation.IsStrongPassword(request.NewPassword), nameof(request.NewPassword), Validation.PasswordRule).ThrowIfInvalid();
        await using var conn = await catalog.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            update catalog.app_user set password_hash = @Hash, must_change_password = true, session_version = session_version + 1,
                   failed_logins = 0, locked_until = null,
                   -- wer Passwort und Telefon verloren hat, richtet die Zwei-Faktor-Anmeldung danach neu ein
                   totp_enabled = false, totp_secret = null, totp_last_step = null, recovery_code_hashes = null
            where id = @Id and tenant_id = @Tenant
            """, new { Id = id, Tenant = tenant.TenantId, Hash = Passwords.Hash(request.NewPassword) }, cancellationToken: ct));
        if (n == 0) throw ApiProblem.NotFound("Benutzer nicht gefunden.");
        await conn.ExecuteAsync(new CommandDefinition("delete from catalog.login_throttle where user_id = @Id", new { Id = id }, cancellationToken: ct));
        await using (var db = await tenant.OpenAsync(ct))
            await tenant.AuditAsync(db, "user.password_reset", "user", id.ToString(), ct: ct);
        return Results.NoContent();
    }

    private static async Task<TenantSettings> LoadSettingsAsync(NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        var row = await conn.QuerySingleAsync<(string Theme, string Language, bool RequireTwoFactor)>(new CommandDefinition(
            "select default_theme, default_language, require_two_factor from catalog.tenant where id = @Id", new { Id = tenantId }, cancellationToken: ct));
        return new TenantSettings(ThemeCatalog.Resolve(row.Theme).Key, Languages.IsKnown(row.Language) ? row.Language : Languages.Default, row.RequireTwoFactor);
    }

    private static async Task<TenantSettings> GetSettingsAsync(TenantContext tenant, CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        return await LoadSettingsAsync(conn, tenant.TenantId, ct);
    }

    private static async Task<TenantSettings> SaveSettingsAsync(TenantSettings request, TenantContext tenant, CatalogDb catalog, CancellationToken ct)
    {
        new Validator()
            .Check(ThemeCatalog.IsKnown(request.DefaultTheme), nameof(request.DefaultTheme), "Unbekanntes Farbschema.")
            .Check(Languages.IsKnown(request.DefaultLanguage), nameof(request.DefaultLanguage), "Unbekannte Sprache.")
            .ThrowIfInvalid();
        await using var conn = await catalog.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "update catalog.tenant set default_theme = @Theme, default_language = @Language, require_two_factor = @RequireTwoFactor where id = @Id",
            new { Id = tenant.TenantId, Theme = request.DefaultTheme, Language = request.DefaultLanguage, request.RequireTwoFactor }, cancellationToken: ct));
        await using (var db = await tenant.OpenAsync(ct))
            await tenant.AuditAsync(db, "tenant.settings_changed", "tenant", tenant.TenantId.ToString(), request, ct: ct);
        return request;
    }
}
