using Dapper;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Contracts.Billing;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace KaiZent.Platform.Api.Features;

/// <summary>Plattform-Administration: Firmendaten des Betreibers und Rechnungsanschriften der Mandanten (nur Katalog, keine Inhalte).</summary>
public static class PlatformCompanyEndpoints
{
    public static void MapPlatformCompany(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization(Policies.Platform).AddEndpointFilter<AccessGuard>();
        group.MapGet(PlatformRoutes.PlatformCompany, GetCompanyAsync).WithName("GetCompanyProfile");
        group.MapPut(PlatformRoutes.PlatformCompany, UpdateCompanyAsync).WithName("UpdateCompanyProfile");
        group.MapGet(PlatformRoutes.PlatformBilling, ListBillingAsync).WithName("ListTenantBilling");
        group.MapPut(PlatformRoutes.PlatformBilling + "/{tenantId:guid}", UpdateBillingAsync).WithName("UpdateTenantBilling");
    }

    internal const string CompanySql = """
        select legal_name, street, postal_code, city, country, email, phone, website, vat_id, tax_number, managing_director, register_court,
               register_number, bank_name, iban, bic, invoice_prefix, payment_term_days, vat_rate, small_business, footer_note, version, updated_at,
               updated_by
        from catalog.company_profile where id = 1
        """;

    internal static Task<CompanyProfile> LoadCompanyAsync(NpgsqlConnection conn, CancellationToken ct, NpgsqlTransaction? tx = null) =>
        conn.QuerySingleAsync<CompanyProfile>(new CommandDefinition(CompanySql, transaction: tx, cancellationToken: ct));

    private static async Task<CompanyProfile> GetCompanyAsync(CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        return await LoadCompanyAsync(conn, ct);
    }

    private static async Task<CompanyProfile> UpdateCompanyAsync(UpdateCompanyProfileRequest r, HttpContext http, CatalogDb catalog, CancellationToken ct)
    {
        new Validator().Add(DocumentRules.Check(r)).ThrowIfInvalid();
        await using var conn = await catalog.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            update catalog.company_profile set legal_name = @LegalName, street = @Street, postal_code = @PostalCode, city = @City, country = @Country,
                email = @Email, phone = @Phone, website = @Website, vat_id = @VatId, tax_number = @TaxNumber, managing_director = @ManagingDirector,
                register_court = @RegisterCourt, register_number = @RegisterNumber, bank_name = @BankName, iban = @Iban, bic = @Bic,
                invoice_prefix = @InvoicePrefix, payment_term_days = @PaymentTermDays, vat_rate = @VatRate, small_business = @SmallBusiness,
                footer_note = @FooterNote, version = version + 1, updated_at = now(), updated_by = @By
            where id = 1 and version = @Version
            """, new
        {
            LegalName = r.LegalName.Trim(), Street = r.Street.Trim(), PostalCode = r.PostalCode.Trim(), City = r.City.Trim(), Country = r.Country.Trim(),
            Email = r.Email.Trim(), Phone = r.Phone.Trim(), Website = r.Website.Trim(), VatId = r.VatId.Trim(), TaxNumber = r.TaxNumber.Trim(),
            ManagingDirector = r.ManagingDirector.Trim(), RegisterCourt = r.RegisterCourt.Trim(), RegisterNumber = r.RegisterNumber.Trim(),
            BankName = r.BankName.Trim(), Iban = DocumentValuesIban(r.Iban), Bic = r.Bic.Trim().ToUpperInvariant(),
            InvoicePrefix = r.InvoicePrefix.Trim(), r.PaymentTermDays, r.VatRate, r.SmallBusiness, FooterNote = r.FooterNote.Trim(),
            By = http.User.DisplayName(), r.Version,
        }, tx, cancellationToken: ct));
        if (n == 0) throw ApiProblem.Stale();
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "company.updated", null, null, ct, tx);
        await tx.CommitAsync(ct);
        return await LoadCompanyAsync(conn, ct);
    }

    private static string DocumentValuesIban(string iban) => new string(iban.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    internal const string BillingSql = """
        select t.id as tenant_id, t.name as tenant_name, t.is_active as tenant_active, b.customer_number,
               coalesce(b.recipient_name, t.name) as recipient_name, coalesce(b.street, '') as street, coalesce(b.postal_code, '') as postal_code,
               coalesce(b.city, '') as city, coalesce(b.country, 'Deutschland') as country,
               case when b.tenant_id is null then t.contact_email else b.billing_email end as billing_email, coalesce(b.vat_id, '') as vat_id,
               coalesce(b.subscription_start, (t.created_at at time zone 'Europe/Berlin')::date) as subscription_start, coalesce(b.version, 0) as version
        from catalog.tenant t left join catalog.tenant_billing b on b.tenant_id = t.id
        """;

    private static async Task<IReadOnlyList<TenantBilling>> ListBillingAsync(CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        return (await conn.QueryAsync<TenantBilling>(new CommandDefinition(BillingSql + " order by lower(t.name)", cancellationToken: ct))).ToList();
    }

    private static async Task<TenantBilling> UpdateBillingAsync(Guid tenantId, UpdateTenantBillingRequest r, HttpContext http, CatalogDb catalog,
        CancellationToken ct)
    {
        new Validator().Add(DocumentRules.Check(r)).ThrowIfInvalid();
        await using var conn = await catalog.OpenAsync(ct);
        if (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition("select exists (select 1 from catalog.tenant where id = @Id)",
                new { Id = tenantId }, cancellationToken: ct)))
            throw ApiProblem.NotFound("Mandant nicht gefunden.");

        var args = new
        {
            Tenant = tenantId, Name = r.RecipientName.Trim(), Street = r.Street.Trim(), PostalCode = r.PostalCode.Trim(), City = r.City.Trim(),
            Country = r.Country.Trim(), Email = string.IsNullOrWhiteSpace(r.BillingEmail) ? null : r.BillingEmail.Trim(), VatId = r.VatId.Trim(),
            Start = r.SubscriptionStart, r.Version,
        };
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = r.Version == 0
            ? await conn.ExecuteAsync(new CommandDefinition("""
                insert into catalog.tenant_billing (tenant_id, recipient_name, street, postal_code, city, country, billing_email, vat_id, subscription_start)
                values (@Tenant, @Name, @Street, @PostalCode, @City, @Country, @Email, @VatId, @Start)
                on conflict (tenant_id) do nothing
                """, args, tx, cancellationToken: ct))
            : await conn.ExecuteAsync(new CommandDefinition("""
                update catalog.tenant_billing set recipient_name = @Name, street = @Street, postal_code = @PostalCode, city = @City, country = @Country,
                    billing_email = @Email, vat_id = @VatId, subscription_start = @Start, version = version + 1, updated_at = now()
                where tenant_id = @Tenant and version = @Version
                """, args, tx, cancellationToken: ct));
        if (n == 0) throw ApiProblem.Stale();
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "billing.updated", tenantId, null, ct, tx);
        await tx.CommitAsync(ct);
        return await conn.QuerySingleAsync<TenantBilling>(new CommandDefinition(BillingSql + " where t.id = @Id", new { Id = tenantId }, cancellationToken: ct));
    }
}
