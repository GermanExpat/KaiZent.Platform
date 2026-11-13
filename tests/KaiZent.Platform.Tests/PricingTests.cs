using System.Net;
using System.Net.Http.Json;
using KaiZent.Platform.Contracts.Billing;
using KaiZent.Platform.Contracts.Common;

namespace KaiZent.Platform.Tests;

public sealed class PackagePriceRuleTests
{
    private static PackagePrice Price(DateOnly from, decimal monthly = 79m, int free = 0, int reduced = 0, decimal? reducedNet = null) =>
        new(Guid.NewGuid(), from, monthly, monthly * 10, free, reduced, reducedNet, null, DateTimeOffset.UtcNow, "Test");

    [Fact]
    public void Intro_phase_runs_free_then_reduced_then_regular()
    {
        var p = Price(new DateOnly(2027, 1, 1), 79m, free: 2, reduced: 10, reducedNet: 49m);
        Assert.Equal(0m, p.MonthlyAmountInMonth(1));
        Assert.Equal(0m, p.MonthlyAmountInMonth(2));
        Assert.Equal(49m, p.MonthlyAmountInMonth(3));
        Assert.Equal(49m, p.MonthlyAmountInMonth(12));
        Assert.Equal(79m, p.MonthlyAmountInMonth(13));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.MonthlyAmountInMonth(0));
    }

    [Fact]
    public void Active_price_is_latest_valid_on_the_day()
    {
        var jan = Price(new DateOnly(2027, 1, 1), 79m);
        var jul = Price(new DateOnly(2027, 7, 1), 89m);
        Assert.Null(PackagePriceRules.ActiveOn([jan, jul], new DateOnly(2026, 12, 31)));
        Assert.Same(jan, PackagePriceRules.ActiveOn([jul, jan], new DateOnly(2027, 6, 30)));
        Assert.Same(jul, PackagePriceRules.ActiveOn([jan, jul], new DateOnly(2027, 7, 1)));
    }

    [Theory]
    [InlineData(-1, 79, 0, 0, null, "ValidFrom")]
    [InlineData(1, 0, 0, 0, null, "MonthlyNet")]
    [InlineData(1, 79.999, 0, 0, null, "MonthlyNet")]
    [InlineData(1, 79, 30, 7, 49.0, "FreeMonths")]
    [InlineData(1, 79, 0, 6, 79.0, "ReducedMonthlyNet")]
    [InlineData(1, 79, 0, 6, null, "ReducedMonthlyNet")]
    [InlineData(1, 79, 3, 0, 49.0, "ReducedMonthlyNet")]
    public void Invalid_prices_are_refused(int days, double monthly, int free, int reduced, double? reducedNet, string field)
    {
        var today = new DateOnly(2026, 11, 13);
        var request = new CreatePackagePriceRequest(today.AddDays(days), (decimal)monthly, 790m, free, reduced, (decimal?)reducedNet);
        Assert.Contains(PackagePriceRules.Check(request, today), p => p.Field == field);
    }

    [Fact]
    public void Price_from_today_with_intro_phase_is_valid()
    {
        var today = new DateOnly(2026, 11, 13);
        Assert.Empty(PackagePriceRules.Check(new CreatePackagePriceRequest(today, 79m, 790m, 2, 10, 49m, "Markteintritt"), today));
    }
}

[Collection(nameof(ApiCollection))]
public sealed class PricingApiTests(ApiFixture api)
{
    private static DateOnly FarFuture() => new DateOnly(2100, 1, 1).AddDays(Random.Shared.Next(0, 30_000));

    [DbFact]
    public async Task Platform_admin_schedules_and_deletes_a_future_price()
    {
        var (admin, _) = await api.LoginAdminAsync();
        var from = FarFuture();
        var resp = await admin.PostAsJsonAsync(PlatformRoutes.PlatformPricing, new CreatePackagePriceRequest(from, 79m, 790m, 2, 10, 49m, "Einführung"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = (await resp.Content.ReadFromJsonAsync<PackagePrice>())!;
        Assert.Equal((from, 79m, 2, 10, 49m), (created.ValidFrom, created.MonthlyNet, created.FreeMonths, created.ReducedMonths, created.ReducedMonthlyNet));

        var pricing = (await admin.GetFromJsonAsync<PackagePricing>(PlatformRoutes.PlatformPricing))!;
        Assert.Contains(pricing.Scheduled, p => p.Id == created.Id);

        // ein zweiter Preis für denselben Tag wird abgewiesen
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync(PlatformRoutes.PlatformPricing, new CreatePackagePriceRequest(from, 89m, 890m))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(PlatformRoutes.PlatformPrice(created.Id))).StatusCode);
        pricing = (await admin.GetFromJsonAsync<PackagePricing>(PlatformRoutes.PlatformPricing))!;
        Assert.DoesNotContain(pricing.Scheduled, p => p.Id == created.Id);
        Assert.True(await api.ScalarAsync<int>("select count(*)::int from catalog.platform_audit where action in ('pricing.created', 'pricing.deleted')") >= 2);
    }

    [DbFact]
    public async Task Price_valid_today_is_current_and_cannot_be_deleted()
    {
        var (admin, _) = await api.LoginAdminAsync();
        var today = (await admin.GetFromJsonAsync<PackagePricing>(PlatformRoutes.PlatformPricing))!.Today;
        var resp = await admin.PostAsJsonAsync(PlatformRoutes.PlatformPricing, new CreatePackagePriceRequest(today, 99m, 990m));
        var created = (await resp.Content.ReadFromJsonAsync<PackagePrice>())!;

        var pricing = (await admin.GetFromJsonAsync<PackagePricing>(PlatformRoutes.PlatformPricing))!;
        Assert.Equal(created.Id, pricing.Current?.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync(PlatformRoutes.PlatformPrice(created.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(PlatformRoutes.PlatformPricing, new CreatePackagePriceRequest(today.AddDays(-1), 99m, 990m))).StatusCode);
    }

    [DbFact]
    public async Task Tenants_cannot_see_or_change_prices()
    {
        var (a, _, _) = await api.NewTenantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync(PlatformRoutes.PlatformPricing)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await a.PostAsJsonAsync(PlatformRoutes.PlatformPricing, new CreatePackagePriceRequest(FarFuture(), 1m, 10m))).StatusCode);
    }
}
