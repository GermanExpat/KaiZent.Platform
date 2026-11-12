using Dapper;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Preferences;
using KaiZent.Platform.Contracts.Security;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KaiZent.Platform.Api.Features;

/// <summary>Anmeldung (mit Zwei-Faktor-Anmeldung), "Ich", Passwort ändern, Abmelden.</summary>
public static class AuthEndpoints
{
    /// <summary>Fehlversuche je Konto und Absender (IP), danach ist das Konto für diesen Absender gesperrt.</summary>
    public const int MaxFailedLogins = 5;

    /// <summary>Fehlversuche je Konto über alle Absender (verteilter Angriff), danach ist das Konto für alle gesperrt.</summary>
    public const int MaxFailedLoginsTotal = 50;

    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    internal sealed record Account(Guid Id, string Email, string DisplayName, string PasswordHash, string Role, bool IsActive,
        int FailedLogins, DateTimeOffset? LockedUntil, Guid? TenantId, string? TenantName, bool? TenantActive, string[]? Modules,
        int SessionVersion, bool MustChangePassword, string? Theme, string? Language, string? Density, string? TenantTheme, string? TenantLanguage,
        bool TotpEnabled, string? TotpSecret, long? TotpLastStep, string[]? RecoveryCodeHashes, bool RequireTwoFactor)
    {
        /// <summary>Pflicht: vom Mandanten verlangt oder Plattform-Administration (wenn <c>Platform:RequirePlatformTwoFactor</c>).</summary>
        public bool TwoFactorRequired(PlatformOptions o) => RequireTwoFactor || (Role == Roles.PlatformAdmin && o.RequirePlatformTwoFactor);

        public bool NeedsSecondFactor(PlatformOptions o) => TotpEnabled || TwoFactorRequired(o);

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
               p.theme as Theme, p.language as Language, p.density as Density, t.default_theme as TenantTheme, t.default_language as TenantLanguage,
               u.totp_enabled as TotpEnabled, u.totp_secret as TotpSecret, u.totp_last_step as TotpLastStep,
               u.recovery_code_hashes as RecoveryCodeHashes, coalesce(t.require_two_factor, false) as RequireTwoFactor
        from catalog.app_user u
        left join catalog.tenant t on t.id = u.tenant_id
        left join catalog.user_preferences p on p.user_id = u.id
        """;

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost(PlatformRoutes.Login, LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Login).WithName("Login");
        app.MapPost(PlatformRoutes.LoginTwoFactor, LoginSecondFactorAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Login).WithName("LoginTwoFactor");
        app.MapPost(PlatformRoutes.LoginTwoFactorSetup, LoginSetupAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Login).WithName("LoginTwoFactorSetup");
        app.MapPost(PlatformRoutes.LoginTwoFactorEnable, LoginEnableAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Login).WithName("LoginTwoFactorEnable");

        var signedIn = app.MapGroup("").RequireAuthorization().AddEndpointFilter<AccessGuard>();
        signedIn.MapGet(PlatformRoutes.Me, MeAsync).WithName("Me");
        signedIn.MapPost(PlatformRoutes.ChangePassword, ChangePasswordAsync).WithName("ChangePassword");
        signedIn.MapPost(PlatformRoutes.Logout, LogoutAsync).WithName("Logout");
        signedIn.MapGet(PlatformRoutes.TwoFactor, StatusAsync).WithName("GetTwoFactor");
        signedIn.MapPost(PlatformRoutes.TwoFactorSetup, SetupAsync).WithName("SetupTwoFactor");
        signedIn.MapPost(PlatformRoutes.TwoFactorEnable, EnableAsync).WithName("EnableTwoFactor");
        signedIn.MapPost(PlatformRoutes.TwoFactorDisable, DisableAsync).WithName("DisableTwoFactor");
    }

    private static IResult Refused() =>
        Results.Json(new ApiError(ErrorCodes.InvalidCredentials, "E-Mail-Adresse oder Passwort ist falsch."), statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Locked() =>
        Results.Json(new ApiError(ErrorCodes.AccountLocked, "Das Konto ist nach mehreren Fehlversuchen vorübergehend gesperrt."), statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext http, CatalogDb catalog, Tokens tokens, TwoFactorService twoFactor,
        IOptions<PlatformOptions> options, TimeProvider time, CancellationToken ct)
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
            return TenantInactive();

        // Zwei-Faktor-Anmeldung eingeschaltet oder Pflicht: 202 mit Challenge statt Token
        if (a.NeedsSecondFactor(options.Value))
            return Results.Json(twoFactor.IssueChallenge(a.Id, setupRequired: !a.TotpEnabled), statusCode: StatusCodes.Status202Accepted);
        return Results.Ok(await CompleteLoginAsync(conn, a, client, tokens, now, ct));
    }

    private static IResult TenantInactive() =>
        Results.Json(new ApiError(ErrorCodes.TenantInactive, "Der Zugang dieses Mandanten ist deaktiviert."), statusCode: StatusCodes.Status403Forbidden);

    private static async Task<LoginResponse> CompleteLoginAsync(NpgsqlConnection conn, Account a, string client, Tokens tokens, DateTimeOffset now, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "update catalog.app_user set failed_logins = 0, locked_until = null, last_login_at = @Now where id = @Id", new { a.Id, Now = now }, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "delete from catalog.login_throttle where user_id = @Id and client = @Client", new { a.Id, Client = client }, cancellationToken: ct));
        return Issue(a, a.SessionVersion, tokens);
    }

    private static IResult ChallengeRefused() =>
        Results.Json(new ApiError(ErrorCodes.Unauthorized, "Die Anmeldung ist abgelaufen. Bitte erneut mit Passwort anmelden."), statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>Konto zur Challenge, nur solange Konto und Mandant aktiv sind und keine Sperre greift.</summary>
    private static async Task<(Account? Account, IResult? Refusal)> AccountOfChallengeAsync(NpgsqlConnection conn, string? challenge, string client,
        TwoFactorService twoFactor, TimeProvider time, CancellationToken ct)
    {
        if (await twoFactor.ReadChallengeAsync(challenge) is not { } userId) return (null, ChallengeRefused());
        var a = await conn.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(AccountSql + " where u.id = @Id", new { Id = userId }, cancellationToken: ct));
        if (a is null || !a.IsActive) return (null, ChallengeRefused());
        if (await IsLockedAsync(conn, a, client, time.GetUtcNow(), ct)) return (null, Locked());
        if (a.TenantId is not null && a.TenantActive != true) return (null, TenantInactive());
        return (a, null);
    }

    private static async Task<IResult> CodeFailureAsync(NpgsqlConnection conn, Account a, string client, DateTimeOffset now, CancellationToken ct)
    {
        await CountFailureAsync(conn, a, client, now, ct);
        return Results.Json(new ApiError(ErrorCodes.InvalidCredentials, "Der Code ist falsch oder abgelaufen."), statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>Zweiter Schritt: Code aus der App (gilt nur einmal) oder ein Wiederherstellungscode (wird verbraucht).</summary>
    private static async Task<IResult> LoginSecondFactorAsync(TwoFactorLoginRequest request, HttpContext http, CatalogDb catalog, Tokens tokens,
        TwoFactorService twoFactor, TimeProvider time, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var client = ClientAddress.Of(http);
        var (a, refusal) = await AccountOfChallengeAsync(conn, request.ChallengeToken, client, twoFactor, time, ct);
        if (a is null) return refusal!;
        if (!a.TotpEnabled || a.TotpSecret is null)
            return Results.Json(new ApiError(ErrorCodes.Conflict, "Für dieses Konto ist die Zwei-Faktor-Anmeldung noch nicht eingerichtet."), statusCode: StatusCodes.Status409Conflict);
        var now = time.GetUtcNow();
        if (await UseTotpAsync(conn, a, request.Code, twoFactor, now, ct) || await UseRecoveryCodeAsync(conn, a, request.Code, ct))
            return Results.Ok(await CompleteLoginAsync(conn, a, client, tokens, now, ct));
        return await CodeFailureAsync(conn, a, client, now, ct);
    }

    /// <summary>Prüft den Code und merkt sich den Zeitschritt atomar: derselbe Code gilt auch bei gleichzeitigen Versuchen nur einmal.</summary>
    private static async Task<bool> UseTotpAsync(NpgsqlConnection conn, Account a, string? code, TwoFactorService twoFactor, DateTimeOffset now, CancellationToken ct)
    {
        if (a.TotpSecret is null) return false;
        if (Totp.Verify(twoFactor.Unprotect(a.TotpSecret), code, now, a.TotpLastStep) is not { } step) return false;
        return await conn.ExecuteAsync(new CommandDefinition(
            "update catalog.app_user set totp_last_step = @Step where id = @Id and (totp_last_step is null or totp_last_step < @Step)",
            new { a.Id, Step = step }, cancellationToken: ct)) == 1;
    }

    /// <summary>Verbraucht einen Wiederherstellungscode atomar; ein zweiter gleichzeitiger Versuch mit demselben Code findet ihn nicht mehr.</summary>
    private static async Task<bool> UseRecoveryCodeAsync(NpgsqlConnection conn, Account a, string? code, CancellationToken ct)
    {
        var hash = RecoveryCodes.Hash(code ?? "");
        if (!(a.RecoveryCodeHashes ?? []).Contains(hash)) return false;
        return await conn.ExecuteAsync(new CommandDefinition(
            "update catalog.app_user set recovery_code_hashes = array_remove(recovery_code_hashes, @Hash) where id = @Id and @Hash = any(recovery_code_hashes)",
            new { a.Id, Hash = hash }, cancellationToken: ct)) == 1;
    }

    /// <summary>Einrichtung während der Anmeldung, wenn die Zwei-Faktor-Anmeldung Pflicht ist.</summary>
    private static async Task<IResult> LoginSetupAsync(TwoFactorLoginRequest request, HttpContext http, CatalogDb catalog, TwoFactorService twoFactor,
        TimeProvider time, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var (a, refusal) = await AccountOfChallengeAsync(conn, request.ChallengeToken, ClientAddress.Of(http), twoFactor, time, ct);
        if (a is null) return refusal!;
        if (a.TotpEnabled) return Results.Json(new ApiError(ErrorCodes.Conflict, "Die Zwei-Faktor-Anmeldung ist schon eingerichtet."), statusCode: StatusCodes.Status409Conflict);
        return Results.Ok(await NewSecretAsync(conn, a, twoFactor, ct));
    }

    private static async Task<IResult> LoginEnableAsync(EnableTwoFactorRequest request, HttpContext http, CatalogDb catalog, TenantConnections tenants,
        Tokens tokens, TwoFactorService twoFactor, TimeProvider time, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var client = ClientAddress.Of(http);
        var (a, refusal) = await AccountOfChallengeAsync(conn, request.ChallengeToken, client, twoFactor, time, ct);
        if (a is null) return refusal!;
        if (a.TotpEnabled) return Results.Json(new ApiError(ErrorCodes.Conflict, "Die Zwei-Faktor-Anmeldung ist schon eingerichtet."), statusCode: StatusCodes.Status409Conflict);
        var now = time.GetUtcNow();
        var codes = await TryEnableAsync(conn, a, request.Code, twoFactor, now, ct);
        if (codes is null) return await CodeFailureAsync(conn, a, client, now, ct);
        await AuditAccountAsync(conn, tenants, a.Id, "user.two_factor_enabled", ct);
        return Results.Ok(new TwoFactorEnabled(codes, await CompleteLoginAsync(conn, a, client, tokens, now, ct)));
    }

    private static async Task<TwoFactorSetup> NewSecretAsync(NpgsqlConnection conn, Account a, TwoFactorService twoFactor, CancellationToken ct)
    {
        var secret = Totp.NewSecret();
        await conn.ExecuteAsync(new CommandDefinition(
            "update catalog.app_user set totp_secret = @Secret, totp_last_step = null where id = @Id and not totp_enabled",
            new { a.Id, Secret = twoFactor.Protect(secret) }, cancellationToken: ct));
        var uri = Totp.OtpAuthUri(secret, a.Email, twoFactor.Issuer);
        return new TwoFactorSetup(secret, uri, Totp.QrSvg(uri));
    }

    /// <summary>Schaltet ein, wenn der Code zum gespeicherten Geheimnis passt; liefert die neuen Wiederherstellungscodes.</summary>
    private static async Task<IReadOnlyList<string>?> TryEnableAsync(NpgsqlConnection conn, Account a, string? code, TwoFactorService twoFactor,
        DateTimeOffset now, CancellationToken ct)
    {
        if (a.TotpSecret is null) return null;
        if (Totp.Verify(twoFactor.Unprotect(a.TotpSecret), code, now, a.TotpLastStep) is not { } step) return null;
        var codes = RecoveryCodes.Create();
        var changed = await conn.ExecuteAsync(new CommandDefinition("""
            update catalog.app_user set totp_enabled = true, totp_last_step = @Step, recovery_code_hashes = @Hashes, failed_logins = 0
            where id = @Id and not totp_enabled and totp_secret = @Secret
            """, new { a.Id, Step = step, Hashes = codes.Select(RecoveryCodes.Hash).ToArray(), Secret = a.TotpSecret }, cancellationToken: ct));
        return changed == 1 ? codes : null;
    }

    private static async Task<TwoFactorStatus> StatusAsync(HttpContext http, CatalogDb catalog, IOptions<PlatformOptions> options, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var a = await CurrentAccountAsync(conn, http, ct);
        return new TwoFactorStatus(a.TotpEnabled, a.RecoveryCodeHashes?.Length ?? 0, a.TwoFactorRequired(options.Value));
    }

    private static async Task<TwoFactorSetup> SetupAsync(HttpContext http, CatalogDb catalog, TwoFactorService twoFactor, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var a = await CurrentAccountAsync(conn, http, ct);
        if (a.TotpEnabled) throw ApiProblem.Conflict("Die Zwei-Faktor-Anmeldung ist schon eingerichtet. Zum Neueinrichten erst abschalten.");
        return await NewSecretAsync(conn, a, twoFactor, ct);
    }

    private static async Task<TwoFactorEnabled> EnableAsync(EnableTwoFactorRequest request, HttpContext http, CatalogDb catalog, TenantConnections tenants,
        TwoFactorService twoFactor, TimeProvider time, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var a = await CurrentAccountAsync(conn, http, ct);
        if (a.TotpEnabled) throw ApiProblem.Conflict("Die Zwei-Faktor-Anmeldung ist schon eingerichtet.");
        if (a.TotpSecret is null) throw ApiProblem.Conflict("Bitte zuerst einrichten (QR-Code anfordern).");
        var codes = await TryEnableAsync(conn, a, request.Code, twoFactor, time.GetUtcNow(), ct)
            ?? throw ApiProblem.Invalid(nameof(request.Code), "Der Code passt nicht. Bitte die Uhrzeit des Telefons prüfen und den aktuellen Code eingeben.");
        await AuditAccountAsync(conn, tenants, a.Id, "user.two_factor_enabled", ct);
        return new TwoFactorEnabled(codes, null);
    }

    /// <summary>Abschalten verlangt Passwort und einen gültigen Code: eine offene Sitzung mit bekanntem Passwort genügt nicht, um den zweiten Faktor zu entfernen.</summary>
    private static async Task<IResult> DisableAsync(DisableTwoFactorRequest request, HttpContext http, CatalogDb catalog, TenantConnections tenants,
        TwoFactorService twoFactor, IOptions<PlatformOptions> options, TimeProvider time, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var a = await CurrentAccountAsync(conn, http, ct);
        if (!Passwords.Verify(a.PasswordHash, request.Password ?? "")) throw ApiProblem.Invalid(nameof(request.Password), "Das Passwort ist falsch.");
        if (a.TwoFactorRequired(options.Value)) throw ApiProblem.Conflict("Für dieses Konto ist die Zwei-Faktor-Anmeldung Pflicht; sie kann nicht abgeschaltet werden.");
        if (a.TotpEnabled && !await UseTotpAsync(conn, a, request.Code, twoFactor, time.GetUtcNow(), ct) && !await UseRecoveryCodeAsync(conn, a, request.Code, ct))
            throw ApiProblem.Invalid(nameof(request.Code), "Bitte den aktuellen Code aus der Authenticator-App oder einen Wiederherstellungscode eingeben.");
        await conn.ExecuteAsync(new CommandDefinition("""
            update catalog.app_user set totp_enabled = false, totp_secret = null, totp_last_step = null, recovery_code_hashes = null where id = @Id
            """, new { a.Id }, cancellationToken: ct));
        await AuditAccountAsync(conn, tenants, a.Id, "user.two_factor_disabled", ct);
        return Results.NoContent();
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
