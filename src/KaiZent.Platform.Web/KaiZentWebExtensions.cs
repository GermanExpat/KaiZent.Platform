using KaiZent.Platform.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace KaiZent.Platform.Web;

/// <summary>
/// Einbindung in die Blazor-App eines Produkts:
/// <code>
/// builder.AddKaiZentWeb(o => { o.ProductName = "KaiZent LMS"; o.ProductShort = "LMS"; o.CookiePrefix = "lms"; }, LmsTexts.Catalog);
/// var app = builder.Build();
/// app.UseKaiZentWeb();                  // Proxy-Header, Schutz-Header, Anmeldung, Sprache, Admin-Adresse, Pflicht-Passwortwechsel
/// app.MapKaiZentAccount();              // Formular-Endpunkte der Konto-Seiten
/// app.MapRazorComponents&lt;App&gt;().AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(KaiZentWebOptions).Assembly);
/// </code>
/// Die Konto-Seiten unter /account rendern statisch (sie setzen Cookies per Formular); die App wählt dafür in App.razor keinen interaktiven Modus.
/// </summary>
public static class KaiZentWebExtensions
{
    public static WebApplicationBuilder AddKaiZentWeb(this WebApplicationBuilder builder, Action<KaiZentWebOptions> configure,
        params TextCatalog[] productTexts)
    {
        var services = builder.Services;
        var cfg = builder.Configuration;
        var options = new KaiZentWebOptions
        {
            ApiBaseUrl = cfg["Api:BaseUrl"] ?? "http://localhost:5310/",
            AdminHost = cfg["Web:AdminHost"],
            KeysPath = cfg["DataProtection:KeysPath"],
        };
        configure(options);
        services.AddSingleton(Options.Create(options));

        services.AddHttpContextAccessor();
        services.AddHttpClient<KaiZentApi>(c =>
        {
            c.BaseAddress = new Uri(options.ApiBaseUrl);
            c.Timeout = TimeSpan.FromMinutes(2);
        });
        services.AddScoped<AuthSession>();
        services.AddSingleton<AdminSite>();
        services.AddSingleton(PlatformTexts.Catalog);
        foreach (var t in productTexts) services.AddSingleton(t);
        services.AddSingleton<Texts>();
        services.Configure<Microsoft.AspNetCore.Builder.RequestLocalizationOptions>(KaiZentCulture.Configure);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.LoginPath = AccountRoutes.Login;
            o.LogoutPath = "/account/logout";
            o.AccessDeniedPath = AccountRoutes.Login;
            o.Cookie.Name = options.AuthCookie;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
                ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.SlidingExpiration = false;
        });
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();

        // Schlüssel für Cookie und Antiforgery auf einem Volume, damit Anmeldungen einen Neustart überleben
        if (options.KeysPath is { Length: > 0 } keys)
            services.AddDataProtection().SetApplicationName("kaizent-" + options.CookiePrefix).PersistKeysToFileSystem(new DirectoryInfo(keys));

        // hinter einem Reverse Proxy: Schema und Client-Adresse aus den Forwarded-Headern, nur aus den Netzen des Proxys
        var proxyNetworks = (cfg["ReverseProxy:KnownNetworks"] ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => System.Net.IPNetwork.TryParse(n, out var net) ? net : throw new InvalidOperationException($"ReverseProxy:KnownNetworks: '{n}' ist kein Netz (CIDR)."))
            .ToList();
        if (cfg.GetValue<bool>("ReverseProxy:Enabled") && proxyNetworks.Count == 0)
            throw new InvalidOperationException("ReverseProxy:Enabled braucht die Netze des Proxys in ReverseProxy:KnownNetworks (z. B. 172.30.0.0/16).");
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var n in proxyNetworks) o.KnownIPNetworks.Add(n);
        });
        return builder;
    }

    /// <summary>Middleware in der richtigen Reihenfolge; vor <c>UseAntiforgery</c> und dem Mapping der Komponenten aufrufen.</summary>
    public static WebApplication UseKaiZentWeb(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("ReverseProxy:Enabled")) app.UseForwardedHeaders();
        app.UseKaiZentSecurityHeaders();
        app.UseAuthentication();
        var adminSite = app.Services.GetRequiredService<AdminSite>();
        app.Use(async (ctx, next) =>
        {
            if (!await adminSite.HandleAsync(ctx)) await next();
        });
        app.UseRequestLocalization();
        // vorläufiges Passwort: bis zur Änderung nur die Konto-Seiten (die API verweigert ohnehin alles andere)
        app.Use(async (ctx, next) =>
        {
            if (ctx.User.FindFirst(AppClaims.MustChangePassword)?.Value == "1" && IsPage(ctx.Request.Path)
                && !ctx.Request.Path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Redirect(AccountRoutes.Password + "?required=1");
                return;
            }
            await next();
        });
        app.UseAuthorization();
        return app;
    }

    /// <summary>Seitenaufrufe, keine Framework- oder statischen Dateien.</summary>
    private static bool IsPage(PathString path) =>
        !path.StartsWithSegments("/_framework") && !path.StartsWithSegments("/_blazor") && !path.StartsWithSegments("/_content")
        && !Path.HasExtension(path.Value);
}
