using Dapper;
using KaiZent.Platform.Api.Documents;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Api.Settings;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Settings;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace KaiZent.Platform.Api.Features;

/// <summary>
/// Plattform-Administration: Systemeinstellungen (E-Mail-Versand, KI-Anbindung). Geheimnisse gehen nur hinein, nie im Klartext heraus;
/// gespeichert werden sie mit der SecretBox verschlüsselt. Ohne gepflegte Werte gelten die Umgebungsvariablen.
/// </summary>
public static class PlatformSettingsEndpoints
{
    public static void MapPlatformSettings(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization(Policies.Platform).AddEndpointFilter<AccessGuard>();
        group.MapGet(PlatformRoutes.PlatformMailSettings, GetMailAsync).WithName("GetMailSettings");
        group.MapPut(PlatformRoutes.PlatformMailSettings, UpdateMailAsync).WithName("UpdateMailSettings");
        group.MapPost(PlatformRoutes.PlatformMailSettingsTest, TestMailAsync).WithName("SendTestMail");
        group.MapGet(PlatformRoutes.PlatformAiSettings, GetAiAsync).WithName("GetAiSettings");
        group.MapPut(PlatformRoutes.PlatformAiSettings, UpdateAiAsync).WithName("UpdateAiSettings");
        group.MapPost(PlatformRoutes.PlatformAiSettingsTest, TestAiAsync).WithName("TestAiConnection");
    }

    // ---- E-Mail-Versand ----

    private static async Task<MailSettings> GetMailAsync(CatalogDb catalog, PlatformSettings settings, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        return await LoadMailAsync(conn, settings, ct);
    }

    private static async Task<MailSettings> LoadMailAsync(NpgsqlConnection conn, PlatformSettings settings, CancellationToken ct)
    {
        var row = await PlatformSettings.LoadMailAsync(conn, ct);
        var active = row.Host.Length > 0 ? settings.Resolve(row) : await settings.GetMailAsync(ct);
        return new MailSettings(row.Host, row.Port, row.UserName, settings.Reveal(row.PasswordProtected, "Das SMTP-Passwort").Length > 0,
            row.FromAddress, row.FromName, row.UseSsl, active.Source, active.IsConfigured, active.Host, active.From, row.Version, row.UpdatedAt,
            row.UpdatedBy);
    }

    private static async Task<MailSettings> UpdateMailAsync(UpdateMailSettingsRequest r, HttpContext http, CatalogDb catalog, PlatformSettings settings,
        CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var current = await PlatformSettings.LoadMailAsync(conn, ct);
        new Validator().Add(SystemSettingsRules.Check(r, current.PasswordProtected is not null)).ThrowIfInvalid();

        var setPassword = r.ClearPassword || !string.IsNullOrEmpty(r.Password);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            update catalog.mail_settings set host = @Host, port = @Port, user_name = @User,
                password_protected = case when @SetPassword then @Password else password_protected end,
                from_address = @From, from_name = @FromName, use_ssl = @UseSsl, version = version + 1, updated_at = now(), updated_by = @By
            where id = 1 and version = @Version
            """, new
        {
            Host = r.Host?.Trim() ?? "", r.Port, User = r.User?.Trim() ?? "", SetPassword = setPassword,
            Password = r.ClearPassword || string.IsNullOrEmpty(r.Password) ? null : settings.Protect(r.Password),
            From = r.From?.Trim() ?? "", FromName = string.IsNullOrWhiteSpace(r.FromName) ? "KaiZent" : r.FromName.Trim(), r.UseSsl,
            By = http.User.DisplayName(), r.Version,
        }, tx, cancellationToken: ct));
        if (n == 0) throw ApiProblem.Stale();
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "settings.mail.updated", null,
            new { Host = r.Host?.Trim(), r.Port, From = r.From?.Trim(), PasswordChanged = setPassword }, ct, tx);
        await tx.CommitAsync(ct);
        await settings.RefreshAsync(ct);
        return await LoadMailAsync(conn, settings, ct);
    }

    private static async Task<SettingsTestResult> TestMailAsync(SendTestMailRequest r, HttpContext http, CatalogDb catalog, IPlatformMailer mailer,
        CancellationToken ct)
    {
        var to = r.To?.Trim();
        new Validator().Check(Validation.IsEmail(to), nameof(r.To), "Bitte eine gültige E-Mail-Adresse angeben.").ThrowIfInvalid();
        SettingsTestResult result;
        if (!mailer.IsConfigured) result = new SettingsTestResult(false, "Der E-Mail-Versand ist nicht eingerichtet (Server und Absender fehlen).");
        else
        {
            try
            {
                await mailer.SendAsync(to!, "KaiZent: Test-E-Mail",
                    "Guten Tag,\n\ndiese Test-E-Mail bestätigt, dass der E-Mail-Versand der KaiZent-Plattform funktioniert.\n\n"
                    + "Diese Nachricht wurde in der Plattform-Administration ausgelöst.", null, null, ct);
                result = new SettingsTestResult(true, $"Die Test-E-Mail an {to} ist verschickt.");
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException)
            {
                result = new SettingsTestResult(false, e.Message);
            }
        }
        await using var conn = await catalog.OpenAsync(ct);
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "settings.mail.tested", null, new { To = to, result.Ok }, ct);
        return result;
    }

    // ---- KI-Anbindung ----

    private static async Task<AiSettings> GetAiAsync(CatalogDb catalog, PlatformSettings settings, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        return await LoadAiAsync(conn, settings, ct);
    }

    private static async Task<AiSettings> LoadAiAsync(NpgsqlConnection conn, PlatformSettings settings, CancellationToken ct)
    {
        var row = await PlatformSettings.LoadAiAsync(conn, ct);
        var active = row.Provider.Length > 0 ? settings.Resolve(row) : await settings.GetAiAsync(ct);
        var key = settings.Reveal(row.ApiKeyProtected, "Der KI-Schlüssel");
        return new AiSettings(row.Provider, key.Length > 0, SystemSettingsRules.Hint(key), row.Model, active.Source, active.Active is not null,
            active.Active?.Name ?? "", active.Active is { } p ? active.ModelFor(p) : "", AiProviders.All, row.Version, row.UpdatedAt, row.UpdatedBy);
    }

    private static async Task<AiSettings> UpdateAiAsync(UpdateAiSettingsRequest r, HttpContext http, CatalogDb catalog, PlatformSettings settings,
        CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var current = await PlatformSettings.LoadAiAsync(conn, ct);
        new Validator().Add(SystemSettingsRules.Check(r, current.ApiKeyProtected is not null)).ThrowIfInvalid();

        var key = r.ApiKey?.Trim() ?? "";
        var setKey = r.ClearApiKey || key.Length > 0;
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            update catalog.ai_settings set provider = @Provider, model = @Model,
                api_key_protected = case when @SetKey then @Key else api_key_protected end,
                version = version + 1, updated_at = now(), updated_by = @By
            where id = 1 and version = @Version
            """, new
        {
            Provider = AiProviders.Find(r.Provider)?.Code ?? "", Model = r.Model?.Trim() ?? "", SetKey = setKey,
            Key = r.ClearApiKey || key.Length == 0 ? null : settings.Protect(key), By = http.User.DisplayName(), r.Version,
        }, tx, cancellationToken: ct));
        if (n == 0) throw ApiProblem.Stale();
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "settings.ai.updated", null,
            new { Provider = AiProviders.Find(r.Provider)?.Code ?? "", Model = r.Model?.Trim(), KeyChanged = setKey }, ct, tx);
        await tx.CommitAsync(ct);
        await settings.RefreshAsync(ct);
        return await LoadAiAsync(conn, settings, ct);
    }

    private static async Task<SettingsTestResult> TestAiAsync(HttpContext http, CatalogDb catalog, PlatformSettings settings, IAiConnectionTester tester,
        CancellationToken ct)
    {
        var result = await tester.TestAsync(await settings.GetAiAsync(ct), ct);
        await using var conn = await catalog.OpenAsync(ct);
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "settings.ai.tested", null, new { result.Ok }, ct);
        return result;
    }
}
