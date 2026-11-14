using System.Net;
using System.Net.Http.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using KaiZent.Platform.Api.Documents;
using KaiZent.Platform.Contracts.Billing;
using KaiZent.Platform.Contracts.Common;
using Npgsql;

namespace KaiZent.Platform.Tests;

public sealed class DocumentRuleTests
{
    [Theory]
    [InlineData("DE89 3704 0044 0532 0130 00", true)]
    [InlineData("de89370400440532013000", true)]
    [InlineData("DE89 3704 0044 0532 0130 01", false)]
    [InlineData("1234", false)]
    public void Iban_checksum_is_verified(string iban, bool valid) => Assert.Equal(valid, DocumentRules.IsIban(iban));

    [Fact]
    public void Totals_round_lines_and_tax_commercially()
    {
        var (net, vat, gross) = DocumentRules.Totals([new DocumentLine("Abo", 1, 99m), new DocumentLine("Credits", 3, 0.335m)], 19m);
        Assert.Equal((100.01m, 19.00m, 119.01m), (net, vat, gross));
    }

    [Fact]
    public void Package_price_follows_intro_phase_of_start_and_regular_price_of_service_month()
    {
        PackagePrice P(int year, int month, decimal monthly, int free = 0, int reduced = 0, decimal? red = null) =>
            new(Guid.NewGuid(), new DateOnly(year, month, 1), monthly, monthly * 10, free, reduced, red, null, DateTimeOffset.UtcNow, "Test");
        var prices = new[] { P(2027, 1, 99m, free: 1, reduced: 2, red: 49m), P(2027, 6, 109m) };
        var start = new DateOnly(2027, 2, 15);
        Assert.Equal(0m, DocumentRules.PackageMonthlyNet(prices, start, new DateOnly(2027, 2, 1)));
        Assert.Equal(49m, DocumentRules.PackageMonthlyNet(prices, start, new DateOnly(2027, 3, 1)));
        Assert.Equal(49m, DocumentRules.PackageMonthlyNet(prices, start, new DateOnly(2027, 4, 1)));
        Assert.Equal(99m, DocumentRules.PackageMonthlyNet(prices, start, new DateOnly(2027, 5, 1)));
        Assert.Equal(109m, DocumentRules.PackageMonthlyNet(prices, start, new DateOnly(2027, 6, 1)));
        Assert.Null(DocumentRules.PackageMonthlyNet(prices, new DateOnly(2026, 12, 1), new DateOnly(2027, 2, 1)));
    }

    [Fact]
    public void Default_templates_offer_only_known_placeholders()
    {
        foreach (var kind in DocumentKinds.TemplateKinds)
        {
            var found = DocxFiller.ReadPlaceholders(DefaultTemplates.For(kind));
            Assert.NotNull(found);
            Assert.NotEmpty(found);
            var known = DocumentPlaceholders.For(kind).Select(p => p.Key).ToHashSet();
            Assert.All(found, key => Assert.Contains(key, known));
        }
        Assert.Contains("Position.Betrag", DocxFiller.ReadPlaceholders(DefaultTemplates.Invoice())!);
        Assert.Null(DocxFiller.ReadPlaceholders([1, 2, 3]));
    }

    [Fact]
    public void Filling_repeats_the_line_row_and_replaces_every_placeholder()
    {
        var company = new CompanyProfile("KaiZent GmbH", "Hauptstr. 1", "10115", "Berlin", "Deutschland", "info@kaizent.example", "030 1", "kaizent.example",
            "DE123456789", "", "Max Muster", "AG Berlin", "HRB 1", "Testbank", "DE89370400440532013000", "COBADEFFXXX", "RE", 14, 19m, false, "", 1,
            DateTimeOffset.UtcNow, "Test");
        var (recipient, facts) = DocumentValues.Sample(DocumentKinds.Invoice, new DateOnly(2027, 3, 5));
        var values = DocumentValues.Build(company, recipient, facts);
        var filled = DocxFiller.Fill(DefaultTemplates.Invoice(), values, DocumentValues.Lines(facts.Lines));

        Assert.Empty(DocxFiller.ReadPlaceholders(filled)!);
        var text = AllText(filled);
        Assert.Contains("KaiZent LMS – Abo März 2027", text);
        Assert.Contains("AI-Credits 250", text);
        Assert.Contains("147,56 €", text);   // 124 € netto + 19 %
        Assert.Contains("IBAN DE89 3704 0044 0532 0130 00", text);
        Assert.Contains("Reiterhof Musterwiese GbR", text);
    }

    [Fact]
    public void Small_business_invoices_carry_the_tax_note()
    {
        var company = new CompanyProfile("Kleinbetrieb", "Weg 1", "12345", "Ort", "Deutschland", "", "", "", "", "12/345/67890", "", "", "", "", "", "",
            "RE", 14, 19m, true, "", 1, DateTimeOffset.UtcNow, "Test");
        var (recipient, facts) = DocumentValues.Sample(DocumentKinds.Invoice, new DateOnly(2027, 3, 5));
        Assert.Contains("§ 19 UStG", DocumentValues.Build(company, recipient, facts)["Rechnung.Steuerhinweis"]);
    }

    internal static string AllText(byte[] docx)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        var parts = new List<string> { string.Join("\n", doc.MainDocumentPart!.Document!.Descendants<Paragraph>().Select(p => p.InnerText)) };
        parts.AddRange(doc.MainDocumentPart.FooterParts.Select(f => f.Footer!.InnerText));
        return string.Join("\n", parts);
    }
}

[Collection(nameof(ApiCollection))]
public sealed class DocumentApiTests(ApiFixture api)
{
    private static UpdateCompanyProfileRequest Company(int version) => new("KaiZent GmbH", "Hauptstr. 1", "10115", "Berlin", "Deutschland",
        "rechnung@kaizent.example", "030 123", "kaizent.example", "DE123456789", "", "Max Muster", "AG Berlin", "HRB 1", "Testbank",
        "DE89 3704 0044 0532 0130 00", "COBADEFFXXX", "RE", 14, 19m, false, "", version);

    private async Task<HttpClient> AdminWithCompanyAsync()
    {
        var (admin, _) = await api.LoginAdminAsync();
        var current = (await admin.GetFromJsonAsync<CompanyProfile>(PlatformRoutes.PlatformCompany))!;
        if (current.IsCompleteForInvoices) return admin;
        var resp = await admin.PutAsJsonAsync(PlatformRoutes.PlatformCompany, Company(current.Version));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return admin;
    }

    private async Task<(Guid TenantId, string Name)> BillableTenantAsync(HttpClient admin, DateOnly start, string? email = "buchhaltung@hof.example")
    {
        var (_, _, tenant) = await api.NewTenantAsync();
        var resp = await admin.PutAsJsonAsync(PlatformRoutes.PlatformTenantBilling(tenant.Id),
            new UpdateTenantBillingRequest(tenant.Name + " GbR", "Feldweg 3", "12345", "Musterdorf", "Deutschland", email, "", start, 0));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (tenant.Id, tenant.Name);
    }

    private async Task<Guid> InvoiceTemplateAsync(HttpClient admin)
    {
        var resp = await admin.PostAsJsonAsync(PlatformRoutes.PlatformTemplates,
            new CreateDocumentTemplateRequest(DocumentKinds.Invoice, "Rechnung " + Guid.NewGuid().ToString("N")[..6]));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<DocumentTemplate>())!.Id;
    }

    [DbFact]
    public async Task Company_profile_is_versioned_and_validated()
    {
        var (admin, _) = await api.LoginAdminAsync();
        var current = (await admin.GetFromJsonAsync<CompanyProfile>(PlatformRoutes.PlatformCompany))!;
        var saved = (await (await admin.PutAsJsonAsync(PlatformRoutes.PlatformCompany, Company(current.Version))).Content.ReadFromJsonAsync<CompanyProfile>())!;
        Assert.Equal("DE89370400440532013000", saved.Iban);
        Assert.True(saved.IsCompleteForInvoices);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(PlatformRoutes.PlatformCompany, Company(current.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync(PlatformRoutes.PlatformCompany, Company(saved.Version) with { Iban = "DE00 1234" })).StatusCode);
    }

    [DbFact]
    public async Task Templates_are_created_from_default_updated_and_previewed()
    {
        var (admin, _) = await api.LoginAdminAsync();
        var id = await InvoiceTemplateAsync(admin);
        var template = (await admin.GetFromJsonAsync<DocumentTemplate>(PlatformRoutes.PlatformTemplate(id)))!;
        Assert.Contains("Rechnung.Nummer", DocxFiller.ReadPlaceholders(Convert.FromBase64String(template.ContentBase64))!);

        var letter = Convert.ToBase64String(DefaultTemplates.Letter());
        var updated = await admin.PutAsJsonAsync(PlatformRoutes.PlatformTemplate(id), new UpdateDocumentTemplateRequest("Neu " + id, "Text", letter, template.Version));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PutAsJsonAsync(PlatformRoutes.PlatformTemplate(id), new UpdateDocumentTemplateRequest("X", null, null, template.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(PlatformRoutes.PlatformTemplate(id),
            new UpdateDocumentTemplateRequest("X", null, Convert.ToBase64String([1, 2, 3]), template.Version + 1))).StatusCode);

        var preview = (await admin.GetFromJsonAsync<DocumentFile>(PlatformRoutes.PlatformTemplatePreview(id)))!;
        Assert.EndsWith(".docx", preview.FileName);
        Assert.Contains("Reiterhof Musterwiese", DocumentRuleTests.AllText(Convert.FromBase64String(preview.ContentBase64)));

        var placeholders = (await admin.GetFromJsonAsync<List<DocumentPlaceholder>>(PlatformRoutes.PlatformTemplatePlaceholders + "?kind=letter"))!;
        Assert.DoesNotContain(placeholders, p => p.Key.StartsWith("Rechnung.", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(PlatformRoutes.PlatformTemplate(id))).StatusCode);
    }

    [DbFact]
    public async Task Invoice_series_is_issued_numbered_sent_paid_and_cancelled()
    {
        var admin = await AdminWithCompanyAsync();
        await using (var conn = new NpgsqlConnection(api.CatalogConnection))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                insert into catalog.package_price (id, valid_from, monthly_net, yearly_net, free_months, created_by)
                values (gen_random_uuid(), '2001-01-01', 50, 500, 1, 'Test') on conflict (valid_from) do nothing
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var running = await BillableTenantAsync(admin, new DateOnly(2001, 1, 15));
        var fresh = await BillableTenantAsync(admin, new DateOnly(2001, 3, 1), email: null);
        var template = await InvoiceTemplateAsync(admin);

        var series = new CreateDocumentSeriesRequest(template, [running.TenantId, fresh.TenantId], "Rechnung", new DateOnly(2001, 3, 31),
            new DateOnly(2001, 3, 1), new DateOnly(2001, 3, 31), [new DocumentLine("KaiZent LMS Abo März", 1, 0m, UsePackagePrice: true),
                new DocumentLine("Einrichtung", 1, 10m)]);
        var resp = await admin.PostAsJsonAsync(PlatformRoutes.PlatformDocumentSeries, series);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var drafts = (await resp.Content.ReadFromJsonAsync<List<IssuedDocument>>())!;
        Assert.Equal(2, drafts.Count);
        var a = drafts.Single(d => d.TenantId == running.TenantId);
        var b = drafts.Single(d => d.TenantId == fresh.TenantId);
        Assert.Equal((60m, 11.40m, 71.40m), (a.Net, a.Vat, a.Gross));   // dritter Monat: 50 € + 10 €
        Assert.Equal(10m, b.Net);                                       // erster Monat kostenlos
        Assert.All(drafts, d => Assert.Equal((DocumentStatus.Draft, (string?)null), (d.Status, d.Number)));

        // Entwurf: Vorschau mit „ENTWURF“ statt Nummer
        var draftFile = (await admin.GetFromJsonAsync<DocumentFile>(PlatformRoutes.PlatformDocumentFile(a.Id)))!;
        Assert.Contains("ENTWURF", DocumentRuleTests.AllText(Convert.FromBase64String(draftFile.ContentBase64)));

        var issued = (await (await admin.PostAsJsonAsync(PlatformRoutes.PlatformDocumentsIssue, new DocumentBatchRequest([a.Id, b.Id])))
            .Content.ReadFromJsonAsync<List<DocumentBatchItem>>())!;
        Assert.All(issued, i => Assert.True(i.Ok, i.Error));
        var numbers = issued.Select(i => i.Document!.Number!).ToList();
        Assert.All(numbers, n => Assert.Matches(@"^RE-2001-\d{5}$", n));
        Assert.Equal(2, numbers.Distinct().Count());
        var issuedA = issued.Single(i => i.Id == a.Id).Document!;
        Assert.Equal(new DateOnly(2001, 4, 14), issuedA.DueDate);
        var text = DocumentRuleTests.AllText(Convert.FromBase64String(
            (await admin.GetFromJsonAsync<DocumentFile>(PlatformRoutes.PlatformDocumentFile(a.Id)))!.ContentBase64));
        Assert.Contains(issuedA.Number!, text);
        Assert.Contains("71,40 €", text);

        // zweimal ausstellen geht nicht, ausgestellte Dokumente sind unveränderlich und nicht löschbar
        Assert.False((await (await admin.PostAsJsonAsync(PlatformRoutes.PlatformDocumentsIssue, new DocumentBatchRequest([a.Id])))
            .Content.ReadFromJsonAsync<List<DocumentBatchItem>>())![0].Ok);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync(PlatformRoutes.PlatformDocument(a.Id))).StatusCode);
        await Assert.ThrowsAsync<PostgresException>(() => api.ScalarAsync<int>(
            "update catalog.issued_document set gross = 1 where id = @Id returning 1", new { Id = a.Id }));

        // Versand: Betrieb ohne E-Mail scheitert einzeln, der andere geht raus
        var sent = (await (await admin.PostAsJsonAsync(PlatformRoutes.PlatformDocumentsSend, new DocumentBatchRequest([a.Id, b.Id])))
            .Content.ReadFromJsonAsync<List<DocumentBatchItem>>())!;
        Assert.True(sent.Single(s => s.Id == a.Id).Ok);
        Assert.False(sent.Single(s => s.Id == b.Id).Ok);
        Assert.Equal(DocumentStatus.Sent, sent.Single(s => s.Id == a.Id).Document!.Status);
        Assert.Contains(api.Mailer.Sent, m => m.To == "buchhaltung@hof.example" && m.Subject.Contains(issuedA.Number!) && m.Attachment is not null);

        var paid = await admin.PostAsJsonAsync(PlatformRoutes.PlatformDocumentPaid(a.Id),
            new MarkPaidRequest(new DateOnly(2001, 4, 10), sent.Single(s => s.Id == a.Id).Document!.Version));
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        var storno = await admin.PostAsync(PlatformRoutes.PlatformDocumentCancel(a.Id), null);
        Assert.Equal(HttpStatusCode.OK, storno.StatusCode);
        var cancellation = (await storno.Content.ReadFromJsonAsync<IssuedDocument>())!;
        Assert.Equal((DocumentKinds.Cancellation, -71.40m, a.Id), (cancellation.Kind, cancellation.Gross, cancellation.CancelsId!.Value));
        Assert.Matches(@"^RE-\d{4}-\d{5}$", cancellation.Number!);
        var all = (await admin.GetFromJsonAsync<List<IssuedDocument>>(PlatformRoutes.PlatformDocuments + "?tenantId=" + running.TenantId))!;
        Assert.Equal(DocumentStatus.Cancelled, all.Single(d => d.Id == a.Id).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(PlatformRoutes.PlatformDocumentCancel(a.Id), null)).StatusCode);

        // die Vorlage ist jetzt in Gebrauch
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync(PlatformRoutes.PlatformTemplate(template))).StatusCode);
    }

    [DbFact]
    public async Task Invoice_series_needs_billing_addresses_and_drafts_can_be_deleted()
    {
        var admin = await AdminWithCompanyAsync();
        var (_, _, bare) = await api.NewTenantAsync();
        var template = await InvoiceTemplateAsync(admin);
        var request = new CreateDocumentSeriesRequest(template, [bare.Id], "Rechnung", new DateOnly(2030, 1, 31), new DateOnly(2030, 1, 1),
            new DateOnly(2030, 1, 31), [new DocumentLine("Beratung", 2, 80m)]);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(PlatformRoutes.PlatformDocumentSeries, request)).StatusCode);

        var billing = (await admin.GetFromJsonAsync<List<TenantBilling>>(PlatformRoutes.PlatformBilling))!.Single(b => b.TenantId == bare.Id);
        Assert.True(billing.IsNew);
        Assert.Equal(bare.Name, billing.RecipientName);

        var tenant = await BillableTenantAsync(admin, new DateOnly(2030, 1, 1));
        var drafts = (await (await admin.PostAsJsonAsync(PlatformRoutes.PlatformDocumentSeries, request with { TenantIds = [tenant.TenantId] }))
            .Content.ReadFromJsonAsync<List<IssuedDocument>>())!;
        Assert.Equal(160m, drafts[0].Net);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(PlatformRoutes.PlatformDocument(drafts[0].Id))).StatusCode);
    }

    [DbFact]
    public async Task Letter_series_has_no_number_and_needs_no_lines()
    {
        var (admin, _) = await api.LoginAdminAsync();
        var (_, _, tenant) = await api.NewTenantAsync();
        var created = await admin.PostAsJsonAsync(PlatformRoutes.PlatformTemplates,
            new CreateDocumentTemplateRequest(DocumentKinds.Letter, "Brief " + Guid.NewGuid().ToString("N")[..6]));
        var template = (await created.Content.ReadFromJsonAsync<DocumentTemplate>())!;
        var drafts = (await (await admin.PostAsJsonAsync(PlatformRoutes.PlatformDocumentSeries,
            new CreateDocumentSeriesRequest(template.Id, [tenant.Id], "Neue Funktionen", new DateOnly(2030, 5, 2))))
            .Content.ReadFromJsonAsync<List<IssuedDocument>>())!;
        var issued = (await (await admin.PostAsJsonAsync(PlatformRoutes.PlatformDocumentsIssue, new DocumentBatchRequest([drafts[0].Id])))
            .Content.ReadFromJsonAsync<List<DocumentBatchItem>>())!.Single();
        Assert.True(issued.Ok, issued.Error);
        Assert.Null(issued.Document!.Number);
        Assert.Equal(DocumentStatus.Issued, issued.Document.Status);
    }

    [DbFact]
    public async Task Tenants_cannot_reach_invoicing()
    {
        var (a, _, t) = await api.NewTenantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync(PlatformRoutes.PlatformCompany)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync(PlatformRoutes.PlatformDocuments)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync(PlatformRoutes.PlatformTemplates)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync(PlatformRoutes.PlatformBilling)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PutAsJsonAsync(PlatformRoutes.PlatformTenantBilling(t.Id),
            new UpdateTenantBillingRequest("X", "Y", "1", "Z", "DE", null, "", new DateOnly(2030, 1, 1), 0))).StatusCode);
    }
}
