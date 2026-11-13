using System.Threading.RateLimiting;
using KaiZent.Platform.Api.Documents;
using KaiZent.Platform.Api.Features;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace KaiZent.Platform.Api;

/// <summary>
/// Einbindung in ein Produkt:
/// <code>
/// builder.AddKaiZentPlatform(new PlatformProduct("KaiZent LMS", "lms_farm_", lmsMigrations, ["equine", "livestock"]));
/// var app = builder.Build();
/// await app.InitializeKaiZentPlatformAsync();
/// var api = app.MapKaiZentPlatform();          // Anmeldung, Einstellungen, Plattform- und Mandanten-Administration
/// var farm = api.MapTenantApi();               // Fachrouten: nur angemeldete Mandanten-Konten, mit AccessGuard
/// </code>
/// </summary>
public static class PlatformExtensions
{
    public static WebApplicationBuilder AddKaiZentPlatform(this WebApplicationBuilder builder, PlatformProduct product)
    {
        DapperSetup.Ensure();
        var services = builder.Services;
        var section = builder.Configuration.GetSection(PlatformOptions.Section);
        services.Configure<PlatformOptions>(section);
        var options = section.Get<PlatformOptions>() ?? new PlatformOptions();

        var catalogConnection = builder.Configuration.GetConnectionString("Catalog")
            ?? throw new InvalidOperationException("ConnectionStrings:Catalog fehlt.");
        var databases = new TenantDatabases(product.DatabasePrefix);
        var catalog = new CatalogDb(catalogConnection, options.Database.CatalogPoolSize);
        services.AddSingleton(product);
        services.AddSingleton(databases);
        services.AddSingleton(catalog);
        services.AddSingleton(TenantConnections.Pooled(catalog, databases, new TenantPoolLimits
        {
            MaxActive = options.Database.MaxActiveTenantConnections,
            MaxPerTenant = options.Database.MaxPerTenant,
            IdleSeconds = options.Database.IdleSeconds,
        }));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Tokens>();
        services.AddSingleton(sp => SecretBox.LoadOrCreate(sp.GetRequiredService<IOptions<PlatformOptions>>().Value.KeysPath));
        services.AddSingleton<TwoFactorService>();
        services.AddSingleton<TenantProvisioner>();
        services.AddSingleton<StartupTasks>();
        // Dokumente: PDF-Umwandlung und E-Mail; ein Produkt kann eigene Umsetzungen vorher registrieren
        var documentOptions = builder.Configuration.GetSection(PlatformOptions.Section + ":Documents").Get<DocumentOptions>() ?? new DocumentOptions();
        services.AddSingleton(documentOptions);
        if (!string.IsNullOrWhiteSpace(documentOptions.LibreOfficePath)) services.TryAddSingleton<IDocumentConverter, LibreOfficeConverter>();
        else services.TryAddSingleton<IDocumentConverter, NoDocumentConverter>();
        services.TryAddSingleton(builder.Configuration.GetSection(PlatformSmtpOptions.Section).Get<PlatformSmtpOptions>() ?? new PlatformSmtpOptions());
        services.TryAddSingleton<IPlatformMailer, SmtpPlatformMailer>();
        services.AddScoped<AccessGuard>();
        services.AddScoped<TenantContext>();
        services.AddScoped<ApiProblemFilter>();
        services.AddHttpContextAccessor();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = Tokens.ValidationOf(options.Jwt);
            });
        services.AddAuthorizationBuilder().AddPlatformPolicies();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // je Absender (Browser-Adresse, von der App weitergereicht): ein Angreifer bremst nur sich selbst
            o.AddPolicy(RateLimits.Login, http => RateLimitPartition.GetFixedWindowLimiter(ClientAddress.Of(http), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, options.LoginRequestsPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
            }));
        });
        return builder;
    }

    /// <summary>Migriert Katalog und Mandanten-Datenbanken und legt bei Bedarf das erste Plattform-Konto an. Vor <c>app.Run()</c> aufrufen.</summary>
    public static async Task InitializeKaiZentPlatformAsync(this WebApplication app)
    {
        _ = Tokens.KeyOf(app.Services.GetRequiredService<IOptions<PlatformOptions>>().Value.Jwt);   // fehlender Schlüssel: Abbruch beim Start
        await app.Services.GetRequiredService<StartupTasks>().RunAsync();
    }

    /// <summary>Middleware und Routen der Plattform. Liefert die Gruppe, unter der das Produkt seine Routen anlegt (mit Fehlerbehandlung).</summary>
    public static RouteGroupBuilder MapKaiZentPlatform(this WebApplication app)
    {
        app.UseApiErrors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        var api = app.MapGroup("").AddEndpointFilter<ApiProblemFilter>();
        api.MapHealth();
        api.MapAuth();
        api.MapPreferences();
        api.MapPlatformTenants();
        api.MapPlatformPricing();
        api.MapPlatformCompany();
        api.MapPlatformTemplates();
        api.MapPlatformDocuments();
        api.MapTenantAdmin();
        return api;
    }

    /// <summary>
    /// Gruppe für Fachrouten eines Mandanten, immer mit AccessGuard. <paramref name="access"/> legt fest, welche Rollen sie nutzen dürfen
    /// (Standard: das Team; der Tierarzt und Kunden nicht).
    /// </summary>
    public static RouteGroupBuilder MapTenantApi(this IEndpointRouteBuilder api, string prefix = "", TenantAccess access = TenantAccess.Team)
    {
        var group = api.MapGroup(prefix).RequireAuthorization(access switch
        {
            TenantAccess.Office => Policies.TenantOffice,
            TenantAccess.TeamAndVetReading => Policies.TenantReader,
            TenantAccess.Owner => Policies.TenantOwner,
            _ => Policies.Tenant,
        }).AddEndpointFilter<AccessGuard>();
        if (access == TenantAccess.TeamAndVetReading) group.AddEndpointFilter<ReadOnlyForVet>();
        return group;
    }

    /// <summary>Gruppe für Fachrouten, die nur die Mandanten-Administration nutzen darf.</summary>
    public static RouteGroupBuilder MapTenantAdminApi(this IEndpointRouteBuilder api, string prefix = "") =>
        api.MapGroup(prefix).RequireAuthorization(Policies.TenantAdmin).AddEndpointFilter<AccessGuard>();
}
