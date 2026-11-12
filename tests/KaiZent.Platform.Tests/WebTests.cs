using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Security;
using KaiZent.Platform.Contracts.Tenants;
using KaiZent.Platform.SampleWeb;
using KaiZent.Platform.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KaiZent.Platform.Tests;

/// <summary>Beispiel-App, deren API-Client direkt in den Test-Server der API zeigt.</summary>
public sealed class SampleWebFactory(ApiFixture api) : WebApplicationFactory<WebProgram>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Api:BaseUrl", "http://api.test/");
        builder.ConfigureTestServices(s => s.AddHttpClient<KaiZentApi>().ConfigurePrimaryHttpMessageHandler(() => api.Server.CreateHandler()));
    }

    public HttpClient Browser(string? language = null)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        if (language is not null) c.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        return c;
    }
}

public sealed class WebUnitTests
{
    [Theory]
    [InlineData("/start", "/start")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData(null, "/")]
    public void Return_urls_stay_local(string? url, string expected) => Assert.Equal(expected, AccountEndpoints.SafeReturnUrl(url));

    [Fact]
    public void Platform_texts_exist_in_both_languages() =>
        Assert.All(PlatformTexts.Catalog.Entries, e => Assert.True(e.Value.De.Length > 0 && e.Value.En.Length > 0, e.Key));
}

[Collection(nameof(ApiCollection))]
public sealed class WebTests(ApiFixture api) : IDisposable
{
    private readonly SampleWebFactory _web = new(api);

    public void Dispose() => _web.Dispose();

    private static async Task<string> FormTokenAsync(HttpClient browser, string page)
    {
        var html = await browser.GetStringAsync(page);
        return Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } t
            ? t : throw new InvalidOperationException("Kein Antiforgery-Token auf " + page);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient browser, string page, string action, params (string, string)[] fields)
    {
        var token = await FormTokenAsync(browser, page);
        var all = fields.Append(("__RequestVerificationToken", token)).Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2));
        return await browser.PostAsync(action, new FormUrlEncodedContent(all));
    }

    private static string Location(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";

    private async Task<(HttpClient Browser, string Email)> SignedInAsync()
    {
        var (_, login, _) = await api.NewTenantAsync();
        var browser = _web.Browser();
        var r = await PostFormAsync(browser, "/account/login", "/account/signin",
            ("email", login.User.Email), ("password", ApiFixture.TenantPassword), ("returnUrl", "/"));
        Assert.Equal("/", Location(r));
        return (browser, login.User.Email);
    }

    [DbFact]
    public async Task Login_page_follows_browser_language_and_switch()
    {
        Assert.Contains("E-Mail-Adresse", await _web.Browser().GetStringAsync("/account/login"));
        var english = _web.Browser("en-US,en;q=0.9");
        Assert.Contains("Email address", await english.GetStringAsync("/account/login"));
        var switched = _web.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await switched.GetAsync("/account/language/en?returnUrl=/account/login")).StatusCode);
        Assert.Contains("Sign in", await switched.GetStringAsync("/account/login"));
    }

    [DbFact]
    public async Task Anonymous_pages_redirect_to_login_and_sign_in_works()
    {
        var anon = await _web.Browser().GetAsync("/");
        Assert.Contains("/account/login", Location(anon));

        var (_, login, _) = await api.NewTenantAsync();
        var browser = _web.Browser();
        var wrong = await PostFormAsync(browser, "/account/login", "/account/signin", ("email", login.User.Email), ("password", "falsch-1234567"));
        Assert.Contains("error=credentials", Location(wrong));
        var forged = await browser.PostAsync("/account/signin", new FormUrlEncodedContent([new("email", login.User.Email), new("password", ApiFixture.TenantPassword)]));
        Assert.Contains("error=form", Location(forged));

        var ok = await PostFormAsync(browser, "/account/login", "/account/signin", ("email", login.User.Email), ("password", ApiFixture.TenantPassword), ("returnUrl", "/"));
        Assert.Equal("/", Location(ok));
        Assert.Contains("Hallo Hof-Admin", await browser.GetStringAsync("/"));

        var logout = await PostFormAsync(browser, "/account", "/account/logout");
        Assert.Contains("/account/login", Location(logout));
        Assert.Contains("/account/login", Location(await browser.GetAsync("/")));
    }

    [DbFact]
    public async Task Password_change_page_shows_and_saves()
    {
        var (browser, email) = await SignedInAsync();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(AccountRoutes.Password)).StatusCode);
        var r = await PostFormAsync(browser, AccountRoutes.Password, "/account/password/change",
            ("currentPassword", ApiFixture.TenantPassword), ("newPassword", "Neu2026Passwort"), ("confirmPassword", "Neu2026Passwort"), ("returnUrl", "/"));
        Assert.Equal("/", Location(r));
        Assert.Contains("Hallo Hof-Admin", await browser.GetStringAsync("/"));
        // das neue Passwort gilt, das alte nicht mehr
        var fresh = _web.Browser();
        var old = await PostFormAsync(fresh, "/account/login", "/account/signin", ("email", email), ("password", ApiFixture.TenantPassword));
        Assert.Contains("error=credentials", Location(old));
        var ok = await PostFormAsync(fresh, "/account/login", "/account/signin", ("email", email), ("password", "Neu2026Passwort"), ("returnUrl", "/"));
        Assert.Equal("/", Location(ok));
    }

    [DbFact]
    public async Task Preferences_change_theme_and_language()
    {
        var (browser, _) = await SignedInAsync();
        var r = await PostFormAsync(browser, "/account", "/account/preferences", ("theme", "fluent-dark-mint"), ("language", "en"), ("density", "compact"));
        Assert.Contains("/account", Location(r));
        var home = await browser.GetStringAsync("/");
        Assert.Contains("fluent-dark-mint", home);
        Assert.Contains("en-GB", home);
        Assert.Contains("Hello", home);
        // unbekanntes Schema wird nicht übernommen
        await PostFormAsync(browser, "/account", "/account/preferences", ("theme", "<script>"), ("language", "de"));
        Assert.Contains("fluent-dark-mint", await browser.GetStringAsync("/"));
    }

    [DbFact]
    public async Task Required_two_factor_is_set_up_during_sign_in()
    {
        var (admin, login, _) = await api.NewTenantAsync();
        var settings = (await admin.GetFromJsonAsync<TenantSettings>(PlatformRoutes.TenantSettings))!;
        (await admin.PutAsJsonAsync(PlatformRoutes.TenantSettings, settings with { RequireTwoFactor = true })).EnsureSuccessStatusCode();

        var browser = _web.Browser();
        var first = await PostFormAsync(browser, "/account/login", "/account/signin", ("email", login.User.Email), ("password", ApiFixture.TenantPassword), ("returnUrl", "/"));
        Assert.StartsWith("/account/2fa-setup", Location(first));

        var page = await browser.GetStringAsync(Location(first));
        Assert.Contains("<svg", page);
        var secret = Regex.Match(page, "class=\"kz-2fa-secret\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var code = Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow));
        var enabled = await browser.PostAsync("/account/signin/2fa-enable", new FormUrlEncodedContent(
            [new("code", code), new("returnUrl", "/"), new("__RequestVerificationToken", token)]));
        Assert.StartsWith("/account/recovery-codes", Location(enabled));

        var codes = await browser.GetStringAsync(Location(enabled));
        Assert.Equal(RecoveryCodes.Count, Regex.Matches(codes, "<li><code>").Count);
        // nur einmal sichtbar
        Assert.Empty(Regex.Matches(await browser.GetStringAsync("/account/recovery-codes"), "<li><code>"));
        Assert.Contains("Hallo", await browser.GetStringAsync("/"));
    }
}
