using System.Net;
using System.Security.Claims;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Preferences;
using KaiZent.Platform.Contracts.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KaiZent.Platform.Web.Services;

/// <summary>Adressen der gemeinsamen Konto-Seiten.</summary>
public static class AccountRoutes
{
    public const string Login = "/account/login";
    public const string TwoFactor = "/account/2fa";
    public const string TwoFactorSetup = "/account/2fa-setup";
    public const string RecoveryCodes = "/account/recovery-codes";
    public const string Password = "/account/password";
    public const string Account = "/account";
}

/// <summary>
/// Formular-Endpunkte der Konto-Seiten: die App holt bei der API ein Token und legt es ins verschlüsselte Anmelde-Cookie. Verlangt die API einen
/// zweiten Faktor, liegt ihre Challenge (5 Minuten gültig) bis dahin verschlüsselt in einem eigenen Cookie. Alle Formulare prüfen das
/// Antiforgery-Token selbst und leiten mit einem Fehlercode zurück, damit die statischen Seiten ihn anzeigen.
/// </summary>
public static class AccountEndpoints
{
    private const string Purpose = "KaiZent.Platform.Web.Account";

    private static KaiZentWebOptions Options(HttpContext ctx) => ctx.RequestServices.GetRequiredService<IOptions<KaiZentWebOptions>>().Value;

    private static IDataProtector Protector(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    /// <summary>Liest ein verschlüsseltes Cookie der Anmeldung; null, wenn es fehlt oder manipuliert ist.</summary>
    public static string? ReadProtected(HttpContext ctx, string name)
    {
        if (ctx.Request.Cookies[name] is not { Length: > 0 } value) return null;
        try { return Protector(ctx).Unprotect(value); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    private static void WriteProtected(HttpContext ctx, string name, string value, TimeSpan lifetime) =>
        ctx.Response.Cookies.Append(name, Protector(ctx).Protect(value), new CookieOptions
        {
            HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/account", MaxAge = lifetime,
        });

    public static void DeleteCookie(HttpContext ctx, string name) => ctx.Response.Cookies.Delete(name, new CookieOptions { Path = "/account" });

    /// <summary>Nur lokale Rücksprungziele ("/…", nicht "//…"), sonst die Startseite.</summary>
    public static string SafeReturnUrl(string? url) =>
        url is { Length: > 0 } && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal) ? url : "/";

    /// <summary>Fehlercode für die Anmeldeseite.</summary>
    public static string ErrorOf(ApiException e) => e.Error.Code switch
    {
        ErrorCodes.AccountLocked => "locked",
        ErrorCodes.TenantInactive => "inactive",
        ErrorCodes.WrongLoginArea => "area",
        ErrorCodes.InvalidCredentials => "credentials",
        _ => e.Status == HttpStatusCode.TooManyRequests ? "throttled" : "unreachable",
    };

    private static string WithReturn(string page, string returnUrl, string? error = null) =>
        page + "?" + (error is null ? "" : "error=" + error + "&") + "returnUrl=" + Uri.EscapeDataString(returnUrl);

    private static string? ClientIpOf(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    public static IEndpointRouteBuilder MapKaiZentAccount(this IEndpointRouteBuilder app)
    {
        app.MapPost("/account/signin", async (HttpContext ctx, KaiZentApi api, IAntiforgery antiforgery, AdminSite adminSite) =>
        {
            if (!await IsGenuineAsync(ctx, antiforgery)) return Results.LocalRedirect(AccountRoutes.Login + "?error=form");
            var form = await ctx.Request.ReadFormAsync();
            var returnUrl = SafeReturnUrl(form["returnUrl"]);
            try
            {
                var (login, challenge) = await api.LoginStartAsync(
                    new LoginRequest(form["email"].ToString(), form["password"].ToString(), adminSite.AreaOf(ctx)), ClientIpOf(ctx), ctx.RequestAborted);
                if (login is not null) return await CompleteAsync(ctx, login, returnUrl);
                WriteProtected(ctx, Options(ctx).ChallengeCookie, challenge!.ChallengeToken, TimeSpan.FromMinutes(5));
                return Results.LocalRedirect(WithReturn(challenge.SetupRequired ? AccountRoutes.TwoFactorSetup : AccountRoutes.TwoFactor, returnUrl));
            }
            catch (ApiException e)
            {
                return Results.LocalRedirect(WithReturn(AccountRoutes.Login, returnUrl, ErrorOf(e)));
            }
            catch (HttpRequestException)
            {
                return Results.LocalRedirect(WithReturn(AccountRoutes.Login, returnUrl, "unreachable"));
            }
        }).AllowAnonymous().DisableAntiforgery();

        app.MapPost("/account/signin/2fa", (HttpContext ctx, KaiZentApi api, IAntiforgery antiforgery) =>
            SecondStepAsync(ctx, antiforgery, AccountRoutes.TwoFactor, async (challenge, code, returnUrl) =>
                await CompleteAsync(ctx, await api.PostAnonymousAsync<LoginResponse>(PlatformRoutes.LoginTwoFactor,
                    new TwoFactorLoginRequest(challenge, code), ClientIpOf(ctx), ctx.RequestAborted), returnUrl)))
            .AllowAnonymous().DisableAntiforgery();

        app.MapPost("/account/signin/2fa-enable", (HttpContext ctx, KaiZentApi api, IAntiforgery antiforgery) =>
            SecondStepAsync(ctx, antiforgery, AccountRoutes.TwoFactorSetup, async (challenge, code, returnUrl) =>
            {
                var enabled = await api.PostAnonymousAsync<TwoFactorEnabled>(PlatformRoutes.LoginTwoFactorEnable,
                    new EnableTwoFactorRequest(code, challenge), ClientIpOf(ctx), ctx.RequestAborted);
                await CompleteAsync(ctx, enabled.Login!, returnUrl);
                WriteProtected(ctx, Options(ctx).RecoveryCodesCookie, string.Join(' ', enabled.RecoveryCodes), TimeSpan.FromMinutes(10));
                return Results.LocalRedirect(WithReturn(AccountRoutes.RecoveryCodes, NextAfter(enabled.Login!, returnUrl)));
            })).AllowAnonymous().DisableAntiforgery();

        // Abmelden: erst das Token bei der API sperren (gilt sonst bis zu seinem Ablauf), dann das Cookie löschen
        app.MapPost("/account/logout", async (HttpContext ctx, KaiZentApi api, IAntiforgery antiforgery) =>
        {
            if (!await IsGenuineAsync(ctx, antiforgery)) return Results.LocalRedirect(AccountRoutes.Login);
            if (ctx.User.Identity?.IsAuthenticated == true)
            {
                try { await api.PostAsync(PlatformRoutes.Logout, null, ctx.RequestAborted); }
                catch (Exception e) when (e is ApiException or HttpRequestException) { }   // Cookie trotzdem löschen
            }
            await ctx.SignOutAsync();
            var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync() : null;
            return Results.LocalRedirect(AccountRoutes.Login + (form?["reason"].ToString() == "idle" ? "?error=idle" : "?signedOut=1"));
        }).DisableAntiforgery();

        // eigenes Passwort ändern (nicht unter /account/password: die Seite selbst nimmt dort POST an, sonst ist die Route doppelt); die API beendet dabei alle anderen Anmeldungen und liefert die neue Anmeldung für diese Sitzung
        app.MapPost("/account/password/change", async (HttpContext ctx, KaiZentApi api, IAntiforgery antiforgery) =>
        {
            if (!await IsGenuineAsync(ctx, antiforgery)) return Results.LocalRedirect(AccountRoutes.Password + "?error=form");
            var form = await ctx.Request.ReadFormAsync();
            var returnUrl = SafeReturnUrl(form["returnUrl"]);
            var newPassword = form["newPassword"].ToString();
            if (newPassword != form["confirmPassword"].ToString()) return Results.LocalRedirect(WithReturn(AccountRoutes.Password, returnUrl, "mismatch"));
            try
            {
                var login = (await api.PostAsync<LoginResponse>(PlatformRoutes.ChangePassword,
                    new ChangePasswordRequest(form["currentPassword"].ToString(), newPassword), ctx.RequestAborted))!;
                await SignInAsync(ctx, login);
                return Results.LocalRedirect(returnUrl);
            }
            catch (ApiException e) when (e.IsUnauthorized)
            {
                return Results.LocalRedirect(AccountRoutes.Login + "?error=expired");
            }
            catch (ApiException e)
            {
                var current = e.FieldErrors.ContainsKey(nameof(ChangePasswordRequest.CurrentPassword));
                return Results.LocalRedirect(WithReturn(AccountRoutes.Password, returnUrl, current ? "current" : "weak"));
            }
            catch (HttpRequestException)
            {
                return Results.LocalRedirect(WithReturn(AccountRoutes.Password, returnUrl, "unreachable"));
            }
        }).RequireAuthorization().DisableAntiforgery();

        // Farbschema, Sprache, Dichte: in der API speichern und ins Cookie übernehmen, damit die nächste Seite sie gleich zeigt
        app.MapPost("/account/preferences", async (HttpContext ctx, KaiZentApi api, IAntiforgery antiforgery) =>
        {
            if (!await IsGenuineAsync(ctx, antiforgery)) return Results.LocalRedirect(AccountRoutes.Account + "?error=form");
            var form = await ctx.Request.ReadFormAsync();
            var returnUrl = SafeReturnUrl(form["returnUrl"]);
            var current = AppClaims.PreferencesOf(ctx.User);
            var wanted = new UserPreferences(
                ThemeCatalog.IsKnown(form["theme"]) ? form["theme"].ToString() : current.Theme,
                Languages.IsKnown(form["language"]) ? form["language"].ToString() : current.Language,
                Densities.IsKnown(form["density"]) ? form["density"].ToString() : current.Density);
            try
            {
                await api.PutAsync(PlatformRoutes.MyPreferences, wanted, ctx.RequestAborted);
                await RefreshAsync(ctx, api);
                return Results.LocalRedirect(returnUrl == "/" ? AccountRoutes.Account + "?saved=1" : returnUrl);
            }
            catch (ApiException e) when (e.IsUnauthorized)
            {
                return Results.LocalRedirect(AccountRoutes.Login + "?error=expired");
            }
            catch (Exception e) when (e is ApiException or HttpRequestException)
            {
                return Results.LocalRedirect(AccountRoutes.Account + "?error=unreachable");
            }
        }).RequireAuthorization().DisableAntiforgery();

        // Sprache vor der Anmeldung (Umschalter auf der Anmeldeseite)
        app.MapGet("/account/language/{code}", (string code, string? returnUrl, HttpContext ctx) =>
        {
            if (Languages.IsKnown(code))
                ctx.Response.Cookies.Append(Options(ctx).LanguageCookie, code, new CookieOptions
                {
                    HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365),
                });
            return Results.LocalRedirect(SafeReturnUrl(returnUrl ?? AccountRoutes.Login));
        }).AllowAnonymous();

        // Zwei-Faktor-Anmeldung im Konto einschalten (Geheimnis hat die Kontoseite bei der API angelegt)
        app.MapPost("/account/two-factor/enable", async (HttpContext ctx, KaiZentApi api, IAntiforgery antiforgery) =>
        {
            if (!await IsGenuineAsync(ctx, antiforgery)) return Results.LocalRedirect(AccountRoutes.Account + "?error=form");
            var form = await ctx.Request.ReadFormAsync();
            try
            {
                var enabled = (await api.PostAsync<TwoFactorEnabled>(PlatformRoutes.TwoFactorEnable,
                    new EnableTwoFactorRequest(form["code"].ToString().Trim()), ctx.RequestAborted))!;
                WriteProtected(ctx, Options(ctx).RecoveryCodesCookie, string.Join(' ', enabled.RecoveryCodes), TimeSpan.FromMinutes(10));
                return Results.LocalRedirect(WithReturn(AccountRoutes.RecoveryCodes, AccountRoutes.Account));
            }
            catch (ApiException e) when (e.IsUnauthorized)
            {
                return Results.LocalRedirect(AccountRoutes.Login + "?error=expired");
            }
            catch (ApiException)
            {
                return Results.LocalRedirect(AccountRoutes.Account + "?setup=1&error=code");
            }
            catch (HttpRequestException)
            {
                return Results.LocalRedirect(AccountRoutes.Account + "?error=unreachable");
            }
        }).RequireAuthorization().DisableAntiforgery();

        app.MapPost("/account/two-factor/disable", async (HttpContext ctx, KaiZentApi api, IAntiforgery antiforgery) =>
        {
            if (!await IsGenuineAsync(ctx, antiforgery)) return Results.LocalRedirect(AccountRoutes.Account + "?error=form");
            var form = await ctx.Request.ReadFormAsync();
            try
            {
                await api.PostAsync(PlatformRoutes.TwoFactorDisable,
                    new DisableTwoFactorRequest(form["password"].ToString(), form["code"].ToString().Trim()), ctx.RequestAborted);
                return Results.LocalRedirect(AccountRoutes.Account + "?saved=1");
            }
            catch (ApiException e) when (e.IsUnauthorized)
            {
                return Results.LocalRedirect(AccountRoutes.Login + "?error=expired");
            }
            catch (ApiException e)
            {
                return Results.LocalRedirect(AccountRoutes.Account + "?error=" + (e.Status == HttpStatusCode.Conflict ? "required" : "disable"));
            }
            catch (HttpRequestException)
            {
                return Results.LocalRedirect(AccountRoutes.Account + "?error=unreachable");
            }
        }).RequireAuthorization().DisableAntiforgery();

        return app;
    }

    /// <summary>Ziel nach der Anmeldung: mit vorläufigem Passwort erst die Passwortänderung.</summary>
    private static string NextAfter(LoginResponse login, string returnUrl) =>
        login.MustChangePassword ? AccountRoutes.Password + "?required=1&returnUrl=" + Uri.EscapeDataString(returnUrl) : returnUrl;

    private static Task SignInAsync(HttpContext ctx, LoginResponse login) =>
        ctx.SignInAsync(AppClaims.PrincipalOf(login), new AuthenticationProperties { ExpiresUtc = login.ExpiresAt, IsPersistent = false });

    private static async Task<IResult> CompleteAsync(HttpContext ctx, LoginResponse login, string returnUrl)
    {
        await SignInAsync(ctx, login);
        DeleteCookie(ctx, Options(ctx).ChallengeCookie);
        return Results.LocalRedirect(NextAfter(login, returnUrl));
    }

    /// <summary>Liest das Konto neu aus der API und schreibt das Cookie mit demselben Token und Ablauf neu (z. B. nach geänderten Einstellungen).</summary>
    private static async Task RefreshAsync(HttpContext ctx, KaiZentApi api)
    {
        var me = (await api.GetAsync<CurrentUser>(PlatformRoutes.Me, ctx.RequestAborted))!;
        var token = ctx.User.FindFirstValue(AppClaims.AccessToken)!;
        var mustChange = ctx.User.FindFirstValue(AppClaims.MustChangePassword) == "1";
        var auth = await ctx.AuthenticateAsync();
        await ctx.SignInAsync(AppClaims.PrincipalOf(me, token, mustChange),
            new AuthenticationProperties { ExpiresUtc = auth.Properties?.ExpiresUtc, IsPersistent = false });
    }

    private static async Task<IResult> SecondStepAsync(HttpContext ctx, IAntiforgery antiforgery, string page, Func<string, string, string, Task<IResult>> step)
    {
        if (!await IsGenuineAsync(ctx, antiforgery)) return Results.LocalRedirect(AccountRoutes.Login + "?error=form");
        var form = await ctx.Request.ReadFormAsync();
        var returnUrl = SafeReturnUrl(form["returnUrl"]);
        var cookie = Options(ctx).ChallengeCookie;
        if (ReadProtected(ctx, cookie) is not { } challenge) return Results.LocalRedirect(WithReturn(AccountRoutes.Login, returnUrl, "expired2fa"));
        try
        {
            return await step(challenge, form["code"].ToString().Trim(), returnUrl);
        }
        catch (ApiException e) when (e.Error.Code == ErrorCodes.InvalidCredentials)
        {
            return Results.LocalRedirect(WithReturn(page, returnUrl, "code"));
        }
        catch (ApiException e) when (e.IsUnauthorized && e.Error.Code != ErrorCodes.AccountLocked)
        {
            DeleteCookie(ctx, cookie);
            return Results.LocalRedirect(WithReturn(AccountRoutes.Login, returnUrl, "expired2fa"));
        }
        catch (ApiException e)
        {
            return Results.LocalRedirect(WithReturn(AccountRoutes.Login, returnUrl, ErrorOf(e)));
        }
        catch (HttpRequestException)
        {
            return Results.LocalRedirect(WithReturn(AccountRoutes.Login, returnUrl, "unreachable"));
        }
    }

    internal static async Task<bool> IsGenuineAsync(HttpContext ctx, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(ctx); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
}
