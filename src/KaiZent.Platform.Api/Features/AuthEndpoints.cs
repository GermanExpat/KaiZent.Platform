using Dapper;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Preferences;
using KaiZent.Platform.Contracts.Security;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace KaiZent.Platform.Api.Features;

/// <summary>Anmeldung, "Ich", Passwort ändern, Abmelden. Zwei-Faktor-Anmeldung folgt im nächsten Schritt (aus dem VMS).</summary>
public static class AuthEndpoints
{
    /// <summary>Fehlversuche je Konto und Absender (IP), danach ist das Konto für diesen Absender gesperrt.</summary>
    public const int MaxFailedLogins = 5;

    /// <summary>Fehlversuche je Konto über alle Absender (verteilter Angriff), danach ist das Konto für alle gesperrt.</summary>
    public const int MaxFailedLoginsTotal = 50;

    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    internal sealed record Account(Guid Id, string Email, string DisplayName, string PasswordHash, string Role, bool IsActive,
        int FailedLogins, DateTimeOffset? LockedUntil, Guid? TenantId, string? TenantName, bool? TenantActive, string[]? Modules,
        int SessionVersion, bool MustChangePassword, string? Theme, string? Language, string? Density, string? TenantTheme, string? TenantLanguage)
    {
        public UserPreferences Preferences => new(
            ThemeCatalog.IsKnown(Theme) ? Theme! : ThemeCatalog.Resolve(TenantTheme).Key,
            Languages.IsKnown(Language) ? Language! : Languages.IsKnown(TenantLanguage) ? TenantLanguage! : Languages.Default,
            Densities.IsKnown(Density) ? Density! : Densities.Normal);

        public CurrentUser ToCurrentUser() => new(Id, Email, DisplayName, Role, TenantId, TenantName, Modules ?? [], Preferences);
    }

    internal const string AccountSql = """
        select u.id as Id, u.email as Email, u.display_name as DisplayName, u.password_hash as PasswordHash, u.role as Role,
               u.is_active as IsActive, u.failed_logins as FailedLogins, u.locked_until as LockedUntil,
               u.tenant_id as TenantId, t.name as TenantName, t.is_active as TenantActive, t.modules as Modules,
               u.session_version as SessionVersion, u.must_change_password as MustChangePassword,
               p.theme as Theme, p.language as Language, p.density as Density, t.default_theme as TenantTheme, t.default_language as TenantLanguage
        from catalog.app_user u
        left join catalog.tenant t on t.id = u.tenant_id
        left join catalog.user_preferences p on p.user_id = u.id
        """;

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost(PlatformRoutes.Login, LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Login).WithName("Login");

        var signedIn = app.MapGroup("").RequireAuthorization().AddEndpointFilter<AccessGuard>();
        signedIn.MapGet(PlatformRoutes.Me, MeAsync).WithName("Me");
        signedIn.MapPost(PlatformRoutes.ChangePassword, ChangePasswordAsync).WithName("ChangePassword");
        signedIn.MapPost(PlatformRoutes.Logout, LogoutAsync).WithName("Logout");
    }

    private static IResult Refused() =>
        Results.Json(new ApiError(ErrorCodes.InvalidCredentials, "E-Mail-Adresse oder Passwort ist falsch."), statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Locked() =>
        Results.Json(new ApiError(ErrorCodes.AccountLocked, "Das Konto ist nach mehreren Fehlversuchen vorübergehend gesperrt."), statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext http, CatalogDb catalog, Tokens tokens, TimeProvider time, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password)) return Refused();

        await using var conn = await catalog.OpenAsync(ct);
        var a = await conn.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(
            AccountSql + """
                 where lower(u.email) = lower(@Email)
                 order by u.is_active desc,
                          -- dieselbe Adresse darf ein Mandanten-Konto und ein Plattform-Konto haben: das zur Anmeldeadresse passende zuerst
                          case when @Area is null then 0 when (u.role = @PlatformRole) = (@Area = @AdminArea) then 0 else 1 end,
                          u.created_at desc
                 limit 1
                """, new { Email = request.Email.Trim(), request.Area, PlatformRole = Roles.PlatformAdmin, AdminArea = LoginAreas.Admin }, cancellationToken: ct));
        var now = time.GetUtcNow();
        var passwordOk = Passwords.Verify(a?.PasswordHash, request.Password);   // läuft immer, auch ohne Konto (gleiche Antwortzeit)
        if (a is null || !a.IsActive) return Refused();
        var client = ClientAddress.Of(http);
        if (await IsLockedAsync(conn, a, client, now, ct)) return Locked();
        if (!passwordOk)
        {
            await CountFailureAsync(conn, a, client, now, ct);
            return Refused();
        }
        if (!LoginAreas.Allows(request.Area, a.Role))
            return Results.Json(new ApiError(ErrorCodes.WrongLoginArea, "Dieses Konto meldet sich unter einer anderen Adresse an."), statusCode: StatusCodes.Status403Forbidden);
        if (a.TenantId is not null && a.TenantActive != true)
            return Results.Json(new ApiError(ErrorCodes.TenantInactive, "Der Zugang dieses Mandanten ist deaktiviert."), statusCode: StatusCodes.Status403Forbidden);

        await conn.ExecuteAsync(new CommandDefinition(
            "update catalog.app_user set failed_logins = 0, locked_until = null, last_login_at = @Now where id = @Id", new { a.Id, Now = now }, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "delete from catalog.login_throttle where user_id = @Id and client = @Client", new { a.Id, Client = client }, cancellationToken: ct));
        return Results.Ok(Issue(a, a.SessionVersion, tokens));
    }

    private static async Task<bool> IsLockedAsync(NpgsqlConnection conn, Account a, string client, DateTimeOffset now, CancellationToken ct)
    {
        if (a.LockedUntil > now) return true;
        var until = await conn.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
            "select locked_until from catalog.login_throttle where user_id = @Id and client = @Client", new { a.Id, Client = client }, cancellationToken: ct));
        return until > now;
    }

    private static async Task CountFailureAsync(NpgsqlConnection conn, Account a, string client, DateTimeOffset now, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into catalog.login_throttle (user_id, client, failures, locked_until, updated_at)
            values (@Id, @Client, 1, null, @Now)
            on conflict (user_id, client) do update set
                failures = case when catalog.login_throttle.failures + 1 >= @Max then 0 else catalog.login_throttle.failures + 1 end,
                locked_until = case when catalog.login_throttle.failures + 1 >= @Max then @Locked else catalog.login_throttle.locked_until end,
                updated_at = @Now
            """, new { a.Id, Client = client, Now = now, Max = MaxFailedLogins, Locked = now + LockDuration }, cancellationToken: ct));
        var failed = a.FailedLogins + 1;
        await conn.ExecuteAsync(new CommandDefinition(
            "update catalog.app_user set failed_logins = @Failed, locked_until = @Locked where id = @Id",
            new { a.Id, Failed = failed >= MaxFailedLoginsTotal ? 0 : failed, Locked = failed >= MaxFailedLoginsTotal ? now + LockDuration : (DateTimeOffset?)null },
            cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "delete from catalog.login_throttle where updated_at < @Before and (locked_until is null or locked_until < @Now)",
            new { Before = now.AddDays(-1), Now = now }, cancellationToken: ct));
    }

    private static LoginResponse Issue(Account a, int sessionVersion, Tokens tokens)
    {
        var (token, expires) = tokens.Issue(a.Id, a.Email, a.DisplayName, a.Role, a.TenantId, sessionVersion);
        return new LoginResponse(token, expires, a.ToCurrentUser(), a.MustChangePassword);
    }

    internal static async Task<Account> CurrentAccountAsync(NpgsqlConnection conn, HttpContext http, CancellationToken ct) =>
        await conn.QuerySingleAsync<Account>(new CommandDefinition(AccountSql + " where u.id = @Id", new { Id = http.User.UserId() }, cancellationToken: ct));

    private static async Task<CurrentUser> MeAsync(HttpContext http, CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        return (await CurrentAccountAsync(conn, http, ct)).ToCurrentUser();
    }

    /// <summary>Eigenes Passwort ändern. Erhöht den Sitzungsstand: alle anderen Anmeldungen enden. Die Antwort ist die neue Anmeldung.</summary>
    private static async Task<LoginResponse> ChangePasswordAsync(ChangePasswordRequest request, HttpContext http, CatalogDb catalog,
        TenantConnections tenants, Tokens tokens, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var a = await CurrentAccountAsync(conn, http, ct);
        if (!Passwords.Verify(a.PasswordHash, request.CurrentPassword ?? ""))
            throw ApiProblem.Invalid(nameof(request.CurrentPassword), "Das bisherige Passwort ist falsch.");
        new Validator()
            .Check(Validation.IsStrongPassword(request.NewPassword), nameof(request.NewPassword), Validation.PasswordRule)
            .Check(request.NewPassword != request.CurrentPassword, nameof(request.NewPassword), "Das neue Passwort muss sich vom bisherigen unterscheiden.")
            .ThrowIfInvalid();
        var version = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            update catalog.app_user set password_hash = @Hash, must_change_password = false, session_version = session_version + 1,
                   failed_logins = 0, locked_until = null
            where id = @Id returning session_version
            """, new { a.Id, Hash = Passwords.Hash(request.NewPassword) }, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("delete from catalog.login_throttle where user_id = @Id", new { a.Id }, cancellationToken: ct));
        await AuditAccountAsync(conn, tenants, a.Id, "user.password_changed", ct);
        return Issue(a with { MustChangePassword = false }, version, tokens);
    }

    /// <summary>Abmelden: das Token kommt bis zu seinem Ablauf auf die Sperrliste.</summary>
    private static async Task<IResult> LogoutAsync(HttpContext http, CatalogDb catalog, TimeProvider time, CancellationToken ct)
    {
        if (http.User.TokenId() is not { Length: > 0 } jti) return Results.NoContent();
        var expires = long.TryParse(http.User.FindFirst("exp")?.Value, out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp) : time.GetUtcNow().AddDays(1);
        await using var conn = await catalog.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "insert into catalog.revoked_token (jti, expires_at) values (@Jti, @Expires) on conflict (jti) do nothing",
            new { Jti = jti, Expires = expires }, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("delete from catalog.revoked_token where expires_at < @Now", new { Now = time.GetUtcNow() }, cancellationToken: ct));
        return Results.NoContent();
    }

    /// <summary>Protokolleintrag im Mandanten des Kontos (die Plattform-Administration hat keinen; sie steht in catalog.platform_audit).</summary>
    internal static async Task AuditAccountAsync(NpgsqlConnection catalogConn, TenantConnections tenants, Guid userId, string action, CancellationToken ct)
    {
        var row = await catalogConn.QuerySingleAsync<(string? Db, string Name)>(new CommandDefinition("""
            select t.database_name, u.display_name from catalog.app_user u left join catalog.tenant t on t.id = u.tenant_id where u.id = @Id
            """, new { Id = userId }, cancellationToken: ct));
        if (row.Db is null)
        {
            await PlatformAudit.WriteAsync(catalogConn, userId, row.Name, action, null, null, ct);
            return;
        }
        await using var db = await tenants.OpenAsync(row.Db, ct);
        await AuditLog.WriteAsync(db, userId, row.Name, action, "user", userId.ToString(), null, null, ct);
    }
}

/// <summary>Protokoll der Plattform-Administration im Katalog: wer hat wann welchen Mandanten angelegt, geändert, deaktiviert. Ohne Inhalte.</summary>
public static class PlatformAudit
{
    public static Task WriteAsync(NpgsqlConnection catalogConn, Guid? userId, string userName, string action, Guid? tenantId, object? details,
        CancellationToken ct, NpgsqlTransaction? tx = null) =>
        catalogConn.ExecuteAsync(new CommandDefinition("""
            insert into catalog.platform_audit (user_id, user_name, action, tenant_id, details) values (@UserId, @UserName, @Action, @TenantId, cast(@Details as jsonb))
            """, new { UserId = userId, UserName = userName, Action = action, TenantId = tenantId,
                Details = details is null ? null : System.Text.Json.JsonSerializer.Serialize(details) }, tx, cancellationToken: ct));
}
