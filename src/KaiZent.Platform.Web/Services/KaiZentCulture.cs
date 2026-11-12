using System.Globalization;
using System.Security.Claims;
using KaiZent.Platform.Contracts.Preferences;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KaiZent.Platform.Web.Services;

/// <summary>
/// Sprache je Anfrage: Einstellung des angemeldeten Kontos, sonst das Sprach-Cookie (Umschalter auf der Anmeldeseite), sonst der Browser,
/// sonst Deutsch. Englisch formatiert Datum und Zahlen britisch (Tag vor Monat), passend zum europäischen Markt.
/// </summary>
public static class KaiZentCulture
{
    public static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public static CultureInfo For(string? language) => language == Languages.English ? English : German;

    public static string? LanguageOf(string? value) =>
        value is null ? null : value.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? Languages.English
            : value.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? Languages.German : null;

    internal sealed class Provider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext ctx)
        {
            var language = ctx.User.Identity?.IsAuthenticated == true ? LanguageOf(ctx.User.FindFirstValue(AppClaims.Language)) : null;
            language ??= LanguageOf(ctx.Request.Cookies[ctx.RequestServices.GetRequiredService<IOptions<KaiZentWebOptions>>().Value.LanguageCookie]);
            // Browser: erste unterstützte Sprache aus Accept-Language (en-US, en-GB, de-AT … zählen als en bzw. de)
            language ??= ctx.Request.GetTypedHeaders().AcceptLanguage
                .OrderByDescending(l => l.Quality ?? 1)
                .Select(l => LanguageOf(l.Value.Value))
                .FirstOrDefault(l => l is not null);
            if (language is null) return NullProviderCultureResult;
            var name = For(language).Name;
            return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(name, name));
        }
    }

    internal static void Configure(RequestLocalizationOptions o)
    {
        var cultures = new[] { German, English };
        o.DefaultRequestCulture = new RequestCulture(German);
        o.SupportedCultures = cultures;
        o.SupportedUICultures = cultures;
        o.FallBackToParentCultures = true;
        o.FallBackToParentUICultures = true;
        o.RequestCultureProviders = [new Provider()];
    }
}
