using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Security;
using KaiZent.Platform.Contracts.Tenants;
using Microsoft.AspNetCore.Hosting;

namespace KaiZent.Platform.Tests;

public sealed class TotpTests
{
    [Fact]
    public void Matches_rfc_6238_reference_value()
    {
        // RFC 6238, Anhang B: Schlüssel "12345678901234567890", T = 59 s → 94287082 (8 Stellen), 6 Stellen: 287082
        var secret = Totp.Base32Encode("12345678901234567890"u8);
        Assert.Equal("287082", Totp.Code(secret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(59))));
    }

    [Fact]
    public void Code_is_valid_only_once_and_within_tolerance()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var step = Totp.StepAt(now);
        Assert.Equal(step, Totp.Verify(secret, Totp.Code(secret, step), now, null));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, step), now, step));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, step + 5), now, null));
        Assert.Null(Totp.Verify(secret, "12345", now, null));
    }

    [Fact]
    public void Recovery_code_hash_ignores_case_and_separator()
    {
        var code = RecoveryCodes.Create()[0];
        Assert.Equal(RecoveryCodes.Hash(code), RecoveryCodes.Hash(code.Replace("-", "").ToLowerInvariant()));
        Assert.Equal(RecoveryCodes.Count, RecoveryCodes.Create().Distinct().Count());
    }
}

[Collection(nameof(ApiCollection))]
public sealed class TwoFactorTests(ApiFixture api)
{
    /// <summary>Zeitschritt einmal je Test festhalten: sonst kippt ein Test, der über eine 30-Sekunden-Grenze läuft.</summary>
    private readonly long _step = Totp.StepAt(DateTimeOffset.UtcNow);

    private string CodeAt(string secret, int stepOffset = 0) => Totp.Code(secret, _step + stepOffset);

    private async Task<(HttpClient Client, string Email)> NewMemberAsync(HttpClient tenantAdmin)
    {
        var email = $"m-{Guid.NewGuid():N}@hof.test";
        (await tenantAdmin.PostAsJsonAsync(PlatformRoutes.TenantUsers,
            new CreateTenantUserRequest(email, "Stallhilfe", Roles.Member, ApiFixture.TenantPassword))).EnsureSuccessStatusCode();
        var (client, _) = await api.LoginAsync(email, ApiFixture.TenantPassword);
        return (client, email);
    }

    private async Task<HttpResponseMessage> PasswordStepAsync(string email, string password, string area = LoginAreas.Tenant) =>
        await api.CreateClient().PostAsJsonAsync(PlatformRoutes.Login, new LoginRequest(email, password, area));

    [DbFact]
    public async Task Setup_login_with_code_and_recovery_code_then_disable()
    {
        var (admin, _, _) = await api.NewTenantAsync();
        var (member, email) = await NewMemberAsync(admin);

        var setup = (await (await member.PostAsync(PlatformRoutes.TwoFactorSetup, null)).Content.ReadFromJsonAsync<TwoFactorSetup>())!;
        Assert.StartsWith("otpauth://totp/", setup.OtpAuthUri);
        Assert.Contains("<svg", setup.QrCodeSvg);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync(PlatformRoutes.TwoFactorEnable, new EnableTwoFactorRequest("000000"))).StatusCode);
        var enabled = (await (await member.PostAsJsonAsync(PlatformRoutes.TwoFactorEnable, new EnableTwoFactorRequest(CodeAt(setup.Secret))))
            .Content.ReadFromJsonAsync<TwoFactorEnabled>())!;
        Assert.Equal(RecoveryCodes.Count, enabled.RecoveryCodes.Count);
        var status = (await member.GetFromJsonAsync<TwoFactorStatus>(PlatformRoutes.TwoFactor))!;
        Assert.True(status.Enabled);
        Assert.False(status.Required);

        // Passwort allein genügt nicht mehr
        var first = await PasswordStepAsync(email, ApiFixture.TenantPassword);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var challenge = (await first.Content.ReadFromJsonAsync<TwoFactorChallenge>())!;
        Assert.False(challenge.SetupRequired);

        // die Challenge ist kein Zugriffstoken
        var misuse = api.CreateClient();
        misuse.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", challenge.ChallengeToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await misuse.GetAsync(PlatformRoutes.Me)).StatusCode);

        // der beim Einschalten benutzte Code gilt nicht noch einmal, der nächste schon
        var anon = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync(PlatformRoutes.LoginTwoFactor, new TwoFactorLoginRequest(challenge.ChallengeToken, CodeAt(setup.Secret)))).StatusCode);
        var ok = await anon.PostAsJsonAsync(PlatformRoutes.LoginTwoFactor, new TwoFactorLoginRequest(challenge.ChallengeToken, CodeAt(setup.Secret, 1)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(string.IsNullOrEmpty((await ok.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken));

        // Wiederherstellungscode: einmal gültig
        var recovery = enabled.RecoveryCodes[0];
        var c2 = (await (await PasswordStepAsync(email, ApiFixture.TenantPassword)).Content.ReadFromJsonAsync<TwoFactorChallenge>())!;
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync(PlatformRoutes.LoginTwoFactor, new TwoFactorLoginRequest(c2.ChallengeToken, recovery))).StatusCode);
        var c3 = (await (await PasswordStepAsync(email, ApiFixture.TenantPassword)).Content.ReadFromJsonAsync<TwoFactorChallenge>())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync(PlatformRoutes.LoginTwoFactor, new TwoFactorLoginRequest(c3.ChallengeToken, recovery))).StatusCode);
        Assert.Equal(RecoveryCodes.Count - 1, (await member.GetFromJsonAsync<TwoFactorStatus>(PlatformRoutes.TwoFactor))!.RecoveryCodesLeft);

        // Abschalten braucht Passwort und Code
        Assert.Equal(HttpStatusCode.BadRequest,
            (await member.PostAsJsonAsync(PlatformRoutes.TwoFactorDisable, new DisableTwoFactorRequest(ApiFixture.TenantPassword, "000000"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await member.PostAsJsonAsync(PlatformRoutes.TwoFactorDisable, new DisableTwoFactorRequest(ApiFixture.TenantPassword, enabled.RecoveryCodes[1]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PasswordStepAsync(email, ApiFixture.TenantPassword)).StatusCode);
    }

    [DbFact]
    public async Task Tenant_policy_forces_setup_during_login_and_reset_clears_it()
    {
        var (admin, _, _) = await api.NewTenantAsync();
        var (_, email) = await NewMemberAsync(admin);
        var settings = (await admin.GetFromJsonAsync<TenantSettings>(PlatformRoutes.TenantSettings))!;
        (await admin.PutAsJsonAsync(PlatformRoutes.TenantSettings, settings with { RequireTwoFactor = true })).EnsureSuccessStatusCode();
        Assert.True((await admin.GetFromJsonAsync<TenantSettings>(PlatformRoutes.TenantSettings))!.RequireTwoFactor);

        var first = await PasswordStepAsync(email, ApiFixture.TenantPassword);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var challenge = (await first.Content.ReadFromJsonAsync<TwoFactorChallenge>())!;
        Assert.True(challenge.SetupRequired);

        var anon = api.CreateClient();
        var setup = (await (await anon.PostAsJsonAsync(PlatformRoutes.LoginTwoFactorSetup, new TwoFactorLoginRequest(challenge.ChallengeToken, "")))
            .Content.ReadFromJsonAsync<TwoFactorSetup>())!;
        var enabled = (await (await anon.PostAsJsonAsync(PlatformRoutes.LoginTwoFactorEnable, new EnableTwoFactorRequest(CodeAt(setup.Secret), challenge.ChallengeToken)))
            .Content.ReadFromJsonAsync<TwoFactorEnabled>())!;
        Assert.NotNull(enabled.Login);

        var member = api.CreateClient();
        member.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enabled.Login!.AccessToken);
        Assert.True((await member.GetFromJsonAsync<TwoFactorStatus>(PlatformRoutes.TwoFactor))!.Required);
        Assert.Equal(HttpStatusCode.Conflict,
            (await member.PostAsJsonAsync(PlatformRoutes.TwoFactorDisable, new DisableTwoFactorRequest(ApiFixture.TenantPassword, enabled.RecoveryCodes[0]))).StatusCode);

        var users = (await admin.GetFromJsonAsync<List<TenantUser>>(PlatformRoutes.TenantUsers))!;
        var user = users.Single(u => u.Email == email);
        Assert.True(user.TwoFactorEnabled);
        // Passwort zurücksetzen (Telefon verloren): die Zwei-Faktor-Anmeldung muss neu eingerichtet werden
        (await admin.PutAsJsonAsync(PlatformRoutes.TenantUsers + $"/{user.Id}/password", new ResetPasswordRequest("Zurueck2026Gesetzt"))).EnsureSuccessStatusCode();
        var again = (await (await PasswordStepAsync(email, "Zurueck2026Gesetzt")).Content.ReadFromJsonAsync<TwoFactorChallenge>())!;
        Assert.True(again.SetupRequired);
    }

    [DbFact]
    public async Task Platform_administration_must_use_two_factor_when_configured()
    {
        using var strict = api.WithWebHostBuilder(b => b.UseSetting("Platform:RequirePlatformTwoFactor", "true"));
        var resp = await strict.CreateClient().PostAsJsonAsync(PlatformRoutes.Login, new LoginRequest(ApiFixture.AdminEmail, ApiFixture.AdminPassword, LoginAreas.Admin));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.True((await resp.Content.ReadFromJsonAsync<TwoFactorChallenge>())!.SetupRequired);
    }
}
