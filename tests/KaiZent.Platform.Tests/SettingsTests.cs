using System.Net;
using System.Net.Http.Json;
using KaiZent.Platform.Api.Settings;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Settings;
using KaiZent.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KaiZent.Platform.Tests;

public sealed class SettingsRuleTests
{
    private static PlatformSettings Settings(PlatformSmtpOptions? mail = null, PlatformAiOptions? ai = null) =>
        new(new CatalogDb("Host=localhost;Database=unused"), SecretBox.FromKey(new byte[32]), mail ?? new PlatformSmtpOptions(), ai ?? new PlatformAiOptions(),
            TimeProvider.System, NullLogger<PlatformSettings>.Instance);

    [Fact]
    public void Environment_is_the_fallback_while_administration_is_empty()
    {
        var settings = Settings(new PlatformSmtpOptions { Host = "smtp.env.test", From = "env@kaizent.test" },
            new PlatformAiOptions { Provider = "Mistral", ApiKey = "env-key-1234567890" });
        Assert.Equal(SettingSources.Environment, settings.Mail.Source);
        Assert.True(settings.Mail.IsConfigured);
        Assert.Equal(AiProviders.Mistral, settings.Ai.Active?.Code);
        Assert.Equal("mistral-medium-latest", settings.Ai.ModelFor(settings.Ai.Active!));

        var empty = new PlatformSettings.MailRow("", 587, "", null, "", "", true, 1, null, "");
        Assert.Equal("smtp.env.test", settings.Resolve(empty).Host);
        var stored = new PlatformSettings.MailRow("smtp.db.test", 465, "u", settings.Protect("pw-1"), "db@kaizent.test", "", true, 2, null, "");
        var resolved = settings.Resolve(stored);
        Assert.Equal((SettingSources.Database, "smtp.db.test", "pw-1", "KaiZent"), (resolved.Source, resolved.Host, resolved.Password, resolved.FromName));

        var ai = settings.Resolve(new PlatformSettings.AiRow("openai", settings.Protect("sk-db-abcdefghijkl"), "gpt-x", 2, null, ""));
        Assert.Equal((SettingSources.Database, AiProviders.OpenAi, "sk-db-abcdefghijkl", "gpt-x"), (ai.Source, ai.Active?.Code, ai.ApiKey, ai.ModelFor(ai.Active!)));
    }

    [Fact]
    public void Secrets_that_do_not_decrypt_count_as_missing()
    {
        var other = SecretBox.FromKey(Enumerable.Repeat((byte)7, 32).ToArray());
        var settings = Settings();
        var ai = settings.Resolve(new PlatformSettings.AiRow("anthropic", other.Protect("sk-ant-1234567890"), "", 1, null, ""));
        Assert.Null(ai.Active);
        Assert.Equal("", ai.ApiKey);
    }

    [Fact]
    public void Rules_require_sender_password_and_key()
    {
        Assert.Empty(SystemSettingsRules.Check(new UpdateMailSettingsRequest("", 587, "", null, false, "", "", true, 1), false));
        Assert.Contains(SystemSettingsRules.Check(new UpdateMailSettingsRequest("smtp.x.test", 587, "", null, false, "kein-mail", "", true, 1), false),
            e => e.Field == "From");
        Assert.Contains(SystemSettingsRules.Check(new UpdateMailSettingsRequest("smtp.x.test", 587, "u", null, false, "a@b.de", "", true, 1), false),
            e => e.Field == "Password");
        Assert.Empty(SystemSettingsRules.Check(new UpdateMailSettingsRequest("smtp.x.test", 587, "u", null, false, "a@b.de", "", true, 1), true));
        Assert.Contains(SystemSettingsRules.Check(new UpdateMailSettingsRequest("smtp.x.test", 0, "", null, false, "a@b.de", "", true, 1), false),
            e => e.Field == "Port");
        Assert.Contains(SystemSettingsRules.Check(new UpdateAiSettingsRequest("gemini", "k", false, "", 1), false), e => e.Field == "Provider");
        Assert.Contains(SystemSettingsRules.Check(new UpdateAiSettingsRequest("mistral", null, false, "", 1), false), e => e.Field == "ApiKey");
        Assert.Contains(SystemSettingsRules.Check(new UpdateAiSettingsRequest("mistral", null, true, "", 1), true), e => e.Field == "ApiKey");
        Assert.Empty(SystemSettingsRules.Check(new UpdateAiSettingsRequest("mistral", null, false, "", 1), true));
        Assert.Equal("…wxyz", SystemSettingsRules.Hint("sk-abcdefghijklmnopqrstuvwxyz"));
        Assert.Equal("", SystemSettingsRules.Hint("short"));
    }
}

[Collection(nameof(ApiCollection))]
public sealed class SettingsApiTests(ApiFixture api)
{
    [DbFact]
    public async Task Mail_settings_store_the_password_encrypted_and_never_return_it()
    {
        var (admin, _) = await api.LoginAdminAsync();
        var before = (await admin.GetFromJsonAsync<MailSettings>(PlatformRoutes.PlatformMailSettings))!;
        Assert.Equal(SettingSources.None, before.Source);

        const string password = "Smtp-Geheimnis-2026";
        var resp = await admin.PutAsJsonAsync(PlatformRoutes.PlatformMailSettings,
            new UpdateMailSettingsRequest(" smtp.kaizent.test ", 465, "versand", password, false, "rechnung@kaizent.test", "KaiZent Abrechnung", true, before.Version));
        resp.EnsureSuccessStatusCode();
        Assert.DoesNotContain(password, await resp.Content.ReadAsStringAsync());
        var saved = (await resp.Content.ReadFromJsonAsync<MailSettings>())!;
        Assert.True(saved.PasswordSet);
        Assert.Equal(("smtp.kaizent.test", SettingSources.Database, true), (saved.Host, saved.Source, saved.IsActive));

        var stored = await api.ScalarAsync<string>("select password_protected from catalog.mail_settings");
        Assert.StartsWith("v1:", stored);
        Assert.DoesNotContain(password, stored);
        Assert.Equal(password, api.Services.GetRequiredService<PlatformSettings>().Mail.Password);

        // leeres Passwort: das hinterlegte bleibt
        resp = await admin.PutAsJsonAsync(PlatformRoutes.PlatformMailSettings,
            new UpdateMailSettingsRequest("smtp.kaizent.test", 587, "versand", "", false, "rechnung@kaizent.test", "KaiZent", true, saved.Version));
        resp.EnsureSuccessStatusCode();
        Assert.Equal(stored, await api.ScalarAsync<string>("select password_protected from catalog.mail_settings"));
        Assert.Equal(587, api.Services.GetRequiredService<PlatformSettings>().Mail.Port);

        // alter Stand und fehlendes Passwort zum Benutzer
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(PlatformRoutes.PlatformMailSettings,
            new UpdateMailSettingsRequest("smtp.kaizent.test", 587, "versand", null, false, "rechnung@kaizent.test", "KaiZent", true, saved.Version))).StatusCode);
        var current = (await admin.GetFromJsonAsync<MailSettings>(PlatformRoutes.PlatformMailSettings))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(PlatformRoutes.PlatformMailSettings,
            new UpdateMailSettingsRequest("smtp.kaizent.test", 587, "versand", null, true, "rechnung@kaizent.test", "KaiZent", true, current.Version))).StatusCode);

        // Test-E-Mail
        var test = await admin.PostAsJsonAsync(PlatformRoutes.PlatformMailSettingsTest, new SendTestMailRequest("marc@kaizent.test"));
        test.EnsureSuccessStatusCode();
        Assert.True((await test.Content.ReadFromJsonAsync<SettingsTestResult>())!.Ok);
        Assert.Contains(api.Mailer.Sent, m => m.To == "marc@kaizent.test" && m.Subject == "KaiZent: Test-E-Mail");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(PlatformRoutes.PlatformMailSettingsTest, new SendTestMailRequest("kein-mail"))).StatusCode);

        // leerer Host: zurück auf die Umgebungsvariablen (hier keine)
        resp = await admin.PutAsJsonAsync(PlatformRoutes.PlatformMailSettings,
            new UpdateMailSettingsRequest("", 587, "", null, true, "", "", true, current.Version));
        resp.EnsureSuccessStatusCode();
        var off = (await resp.Content.ReadFromJsonAsync<MailSettings>())!;
        Assert.Equal((SettingSources.None, false, false), (off.Source, off.IsActive, off.PasswordSet));
        Assert.Equal(3, await api.ScalarAsync<int>("select count(*)::int from catalog.platform_audit where action = 'settings.mail.updated'"));
    }

    [DbFact]
    public async Task Ai_settings_keep_the_key_secret_and_test_with_the_stored_key()
    {
        var (admin, _) = await api.LoginAdminAsync();
        var before = (await admin.GetFromJsonAsync<AiSettings>(PlatformRoutes.PlatformAiSettings))!;
        Assert.Equal(SettingSources.None, before.Source);
        Assert.Equal(AiProviders.All.Count, before.Providers.Count);

        var test = await admin.PostAsync(PlatformRoutes.PlatformAiSettingsTest, null);
        Assert.False((await test.Content.ReadFromJsonAsync<SettingsTestResult>())!.Ok);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(PlatformRoutes.PlatformAiSettings,
            new UpdateAiSettingsRequest("mistral", null, false, "", before.Version))).StatusCode);

        const string key = "mistral-key-abcdefghijwxyz";
        var resp = await admin.PutAsJsonAsync(PlatformRoutes.PlatformAiSettings, new UpdateAiSettingsRequest("Mistral", key, false, "", before.Version));
        resp.EnsureSuccessStatusCode();
        Assert.DoesNotContain(key, await resp.Content.ReadAsStringAsync());
        var saved = (await resp.Content.ReadFromJsonAsync<AiSettings>())!;
        Assert.Equal((AiProviders.Mistral, true, "…wxyz", SettingSources.Database, true, "mistral-medium-latest"),
            (saved.Provider, saved.ApiKeySet, saved.ApiKeyHint, saved.Source, saved.IsActive, saved.ActiveModel));
        Assert.DoesNotContain(key, await api.ScalarAsync<string>("select api_key_protected from catalog.ai_settings"));

        // Modell ändern, Schlüssel bleibt
        resp = await admin.PutAsJsonAsync(PlatformRoutes.PlatformAiSettings, new UpdateAiSettingsRequest("mistral", null, false, "mistral-large-latest", saved.Version));
        resp.EnsureSuccessStatusCode();
        test = await admin.PostAsync(PlatformRoutes.PlatformAiSettingsTest, null);
        Assert.True((await test.Content.ReadFromJsonAsync<SettingsTestResult>())!.Ok);
        Assert.Equal((key, "mistral-large-latest"), (api.AiTester.Last!.ApiKey, api.AiTester.Last.Model));

        // nur die Plattform-Administration
        var (tenant, _, _) = await api.NewTenantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.GetAsync(PlatformRoutes.PlatformAiSettings)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.GetAsync(PlatformRoutes.PlatformMailSettings)).StatusCode);
    }
}
