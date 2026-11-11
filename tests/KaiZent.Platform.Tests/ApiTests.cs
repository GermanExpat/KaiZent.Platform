using System.Net;
using System.Net.Http.Json;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Preferences;
using KaiZent.Platform.Contracts.Security;
using KaiZent.Platform.Contracts.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace KaiZent.Platform.Tests;

[Collection(nameof(ApiCollection))]
public sealed class ApiTests(ApiFixture api)
{
    [DbFact]
    public async Task Every_endpoint_except_login_and_health_requires_authorization()
    {
        _ = api.CreateClient();
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);
        var open = endpoints.Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null).Select(e => e.RoutePattern.RawText).ToList();
        Assert.Equal(new[] { PlatformRoutes.Health, PlatformRoutes.Login }.Order(StringComparer.Ordinal), open.Order(StringComparer.Ordinal));
        // alle anderen tragen eine Autorisierung (eigene Policy oder die Rückfall-Policy "angemeldet") und den AccessGuard
        foreach (var e in endpoints.Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null))
            Assert.True(e.Metadata.GetMetadata<IAuthorizeData>() is not null, $"{e.RoutePattern.RawText} ohne RequireAuthorization");
    }

    [DbFact]
    public async Task Health_is_open_and_other_routes_need_login()
    {
        var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(PlatformRoutes.Health)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(PlatformRoutes.Me)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(PlatformRoutes.PlatformTenants)).StatusCode);
    }

    [DbFact]
    public async Task Platform_admin_creates_tenant_with_own_database()
    {
        var (_, login, tenant) = await api.NewTenantAsync("notes", "extra");
        Assert.Equal(Roles.TenantAdmin, login.User.Role);
        Assert.Equal(tenant.Id, login.User.TenantId);
        Assert.Equal(["notes", "extra"], login.User.TenantModules);
        var db = await api.ScalarAsync<string>("select database_name from catalog.tenant where id = @Id", new { tenant.Id });
        Assert.Equal("kzsample_t_" + tenant.Id.ToString("N"), db);
        Assert.Equal(1, await api.ScalarAsync<int>("select count(*)::int from pg_database where datname = @Db", new { Db = db }));
    }

    [DbFact]
    public async Task Unknown_module_is_refused()
    {
        var (admin, _) = await api.LoginAdminAsync();
        var resp = await admin.PostAsJsonAsync(PlatformRoutes.PlatformTenants,
            new CreateTenantRequest("Hof X", ["horses"], $"x-{Guid.NewGuid():N}@hof.test", "X", ApiFixture.TenantPassword));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [DbFact]
    public async Task Tenants_only_see_their_own_data_and_platform_sees_none()
    {
        var (a, _, _) = await api.NewTenantAsync();
        var (b, _, _) = await api.NewTenantAsync();
        (await a.PostAsJsonAsync("/api/sample/notes", new SampleNote(Guid.Empty, "Heu bestellen"))).EnsureSuccessStatusCode();

        Assert.Single((await a.GetFromJsonAsync<List<SampleNote>>("/api/sample/notes"))!);
        Assert.Empty((await b.GetFromJsonAsync<List<SampleNote>>("/api/sample/notes"))!);

        var (admin, _) = await api.LoginAdminAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/sample/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(PlatformRoutes.TenantUsers)).StatusCode);
    }

    [DbFact]
    public async Task Tenant_users_cannot_use_platform_administration()
    {
        var (a, _, _) = await api.NewTenantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync(PlatformRoutes.PlatformTenants)).StatusCode);
    }

    [DbFact]
    public async Task Module_must_be_enabled()
    {
        var (a, _, _) = await api.NewTenantAsync("extra");
        var resp = await a.GetAsync("/api/sample/notes");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [DbFact]
    public async Task Deactivating_a_tenant_ends_access_immediately()
    {
        var (a, _, tenant) = await api.NewTenantAsync();
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/sample/notes")).StatusCode);
        var (admin, _) = await api.LoginAdminAsync();
        (await admin.PutAsJsonAsync(PlatformRoutes.PlatformTenantStatus(tenant.Id), new SetActiveRequest(false))).EnsureSuccessStatusCode();
        var resp = await a.GetAsync("/api/sample/notes");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(ErrorCodes.TenantInactive, (await resp.Content.ReadFromJsonAsync<ApiError>())!.Code);
        (await admin.PutAsJsonAsync(PlatformRoutes.PlatformTenantStatus(tenant.Id), new SetActiveRequest(true))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/sample/notes")).StatusCode);
    }

    [DbFact]
    public async Task Changes_are_audited_in_the_tenant()
    {
        var (a, _, _) = await api.NewTenantAsync();
        (await a.PostAsJsonAsync("/api/sample/notes", new SampleNote(Guid.Empty, "Impfung Stute"))).EnsureSuccessStatusCode();
        (await a.PostAsJsonAsync(PlatformRoutes.TenantUsers,
            new CreateTenantUserRequest($"m-{Guid.NewGuid():N}@hof.test", "Stallhilfe", Roles.Member, ApiFixture.TenantPassword))).EnsureSuccessStatusCode();
        // Platform-Protokoll im Katalog: Anlage des Mandanten, ohne Inhalte
        Assert.True(await api.ScalarAsync<int>("select count(*)::int from catalog.platform_audit where action = 'tenant.created'") > 0);
    }

    [DbFact]
    public async Task Tenant_admin_manages_users_and_role_change_ends_old_session()
    {
        var (a, _, _) = await api.NewTenantAsync();
        var email = $"m-{Guid.NewGuid():N}@hof.test";
        var created = await (await a.PostAsJsonAsync(PlatformRoutes.TenantUsers,
            new CreateTenantUserRequest(email, "Stallhilfe", Roles.Member, ApiFixture.TenantPassword))).Content.ReadFromJsonAsync<TenantUser>();
        var (member, _) = await api.LoginAsync(email, ApiFixture.TenantPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(PlatformRoutes.TenantUsers)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/sample/notes")).StatusCode);

        (await a.PutAsJsonAsync(PlatformRoutes.TenantUser(created!.Id), new UpdateTenantUserRequest("Stallhilfe", Roles.TenantAdmin))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/sample/notes")).StatusCode);

        (await a.PutAsJsonAsync(PlatformRoutes.TenantUserStatus(created.Id), new SetActiveRequest(false))).EnsureSuccessStatusCode();
        var refused = await api.CreateClient().PostAsJsonAsync(PlatformRoutes.Login, new LoginRequest(email, ApiFixture.TenantPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [DbFact]
    public async Task Users_of_other_tenants_are_invisible()
    {
        var (a, _, _) = await api.NewTenantAsync();
        var (_, bLogin, _) = await api.NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await a.PutAsJsonAsync(PlatformRoutes.TenantUserStatus(bLogin.User.Id), new SetActiveRequest(false))).StatusCode);
    }

    [DbFact]
    public async Task Preferences_are_validated_and_follow_the_account()
    {
        var (a, login, _) = await api.NewTenantAsync();
        Assert.Equal(ThemeCatalog.Default, login.User.Preferences.Theme);
        Assert.Equal(Languages.German, login.User.Preferences.Language);

        var options = await a.GetFromJsonAsync<PreferenceOptions>(PlatformRoutes.PreferenceOptions);
        Assert.Equal(ThemeCatalog.All.Count, options!.Themes.Count);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.PutAsJsonAsync(PlatformRoutes.MyPreferences, new UserPreferences("eigenes-rot", Languages.English, Densities.Normal))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await a.PutAsJsonAsync(PlatformRoutes.MyPreferences, new UserPreferences("fluent-dark-mint", "fr", Densities.Normal))).StatusCode);
        (await a.PutAsJsonAsync(PlatformRoutes.MyPreferences, new UserPreferences("fluent-dark-mint", Languages.English, Densities.Compact))).EnsureSuccessStatusCode();

        var (_, again) = await api.LoginAsync(login.User.Email, ApiFixture.TenantPassword);
        Assert.Equal(new UserPreferences("fluent-dark-mint", Languages.English, Densities.Compact), again.User.Preferences);
    }

    [DbFact]
    public async Task New_users_start_with_tenant_default_theme()
    {
        var (a, _, _) = await api.NewTenantAsync();
        (await a.PutAsJsonAsync(PlatformRoutes.TenantSettings, new TenantSettings("classic-office-white", Languages.English))).EnsureSuccessStatusCode();
        var email = $"m-{Guid.NewGuid():N}@hof.test";
        (await a.PostAsJsonAsync(PlatformRoutes.TenantUsers, new CreateTenantUserRequest(email, "Reitlehrerin", Roles.Member, ApiFixture.TenantPassword)))
            .EnsureSuccessStatusCode();
        var (_, login) = await api.LoginAsync(email, ApiFixture.TenantPassword);
        Assert.Equal("classic-office-white", login.User.Preferences.Theme);
        Assert.Equal(Languages.English, login.User.Preferences.Language);
    }

    [DbFact]
    public async Task Account_locks_after_repeated_failures()
    {
        var (_, login, _) = await api.NewTenantAsync();
        var client = api.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(PlatformRoutes.Login, new LoginRequest(login.User.Email, "falsch-123456"))).StatusCode);
        var resp = await client.PostAsJsonAsync(PlatformRoutes.Login, new LoginRequest(login.User.Email, ApiFixture.TenantPassword));
        Assert.Equal(ErrorCodes.AccountLocked, (await resp.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [DbFact]
    public async Task Logout_and_password_change_end_sessions()
    {
        var (a, login, _) = await api.NewTenantAsync();
        var (second, _) = await api.LoginAsync(login.User.Email, ApiFixture.TenantPassword);
        var changed = await a.PostAsJsonAsync(PlatformRoutes.ChangePassword, new ChangePasswordRequest(ApiFixture.TenantPassword, "Neues2026Passwort"));
        changed.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync(PlatformRoutes.Me)).StatusCode);

        var (third, _) = await api.LoginAsync(login.User.Email, "Neues2026Passwort");
        Assert.Equal(HttpStatusCode.NoContent, (await third.PostAsync(PlatformRoutes.Logout, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await third.GetAsync(PlatformRoutes.Me)).StatusCode);
    }

    [DbFact]
    public async Task Same_address_may_exist_once_per_area()
    {
        var (a, _, _) = await api.NewTenantAsync();
        var resp = await a.PostAsJsonAsync(PlatformRoutes.TenantUsers,
            new CreateTenantUserRequest(ApiFixture.AdminEmail, "Gleiche Adresse", Roles.Member, ApiFixture.TenantPassword));
        resp.EnsureSuccessStatusCode();
        var admin = await api.CreateClient().PostAsJsonAsync(PlatformRoutes.Login, new LoginRequest(ApiFixture.AdminEmail, ApiFixture.AdminPassword, LoginAreas.Admin));
        Assert.Equal(Roles.PlatformAdmin, (await admin.Content.ReadFromJsonAsync<LoginResponse>())!.User.Role);
        var wrong = await api.CreateClient().PostAsJsonAsync(PlatformRoutes.Login, new LoginRequest(ApiFixture.AdminEmail, ApiFixture.AdminPassword, LoginAreas.Tenant));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);   // Mandanten-Konto mit anderem Passwort
    }
}
