using System.Text.Json;
using Dapper;
using KaiZent.Platform.Api.Documents;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Contracts.Billing;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace KaiZent.Platform.Api.Features;

/// <summary>
/// Plattform-Administration: Rechnungen und Serienbriefe an Mandanten. Ein Serienlauf erzeugt je ausgewähltem Mandanten einen Entwurf aus
/// einer Vorlage. Beim Ausstellen bekommt eine Rechnung ihre fortlaufende Nummer, die Datei wird erzeugt und gespeichert; danach ist das
/// Dokument unveränderlich (Datenbank-Trigger). Korrigiert wird per Stornorechnung.
/// </summary>
public static class PlatformDocumentEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapPlatformDocuments(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization(Policies.Platform).AddEndpointFilter<AccessGuard>();
        group.MapGet(PlatformRoutes.PlatformDocuments, ListAsync).WithName("ListIssuedDocuments");
        group.MapPost(PlatformRoutes.PlatformDocumentSeries, CreateSeriesAsync).WithName("CreateDocumentSeries");
        group.MapPost(PlatformRoutes.PlatformDocumentsIssue, IssueAsync).WithName("IssueDocuments");
        group.MapPost(PlatformRoutes.PlatformDocumentsSend, SendAsync).WithName("SendDocuments");
        group.MapGet(PlatformRoutes.PlatformDocuments + "/{id:guid}/file", FileAsync).WithName("GetIssuedDocumentFile");
        group.MapPost(PlatformRoutes.PlatformDocuments + "/{id:guid}/paid", MarkPaidAsync).WithName("MarkDocumentPaid");
        group.MapPost(PlatformRoutes.PlatformDocuments + "/{id:guid}/cancel", CancelAsync).WithName("CancelInvoice");
        group.MapDelete(PlatformRoutes.PlatformDocuments + "/{id:guid}", DeleteAsync).WithName("DeleteDraftDocument");
    }

    private sealed record Row(Guid Id, Guid RunId, string Kind, Guid TenantId, string TenantName, Guid TemplateId, string Title, string? Number,
        string Status, DateOnly IssueDate, DateOnly? ServiceFrom, DateOnly? ServiceTo, DateOnly? DueDate, string LinesJson, decimal Net, decimal VatRate,
        decimal Vat, decimal Gross, Guid? CancelsId, string? FileType, DateTimeOffset? SentAt, string? SentTo, DateOnly? PaidOn, DateTimeOffset CreatedAt,
        string CreatedBy, int Version)
    {
        public IReadOnlyList<DocumentLine> Lines => JsonSerializer.Deserialize<List<DocumentLine>>(LinesJson, Json) ?? [];

        public IssuedDocument ToDocument() => new(Id, RunId, Kind, TenantId, TenantName, TemplateId, Title, Number, Status, IssueDate, ServiceFrom,
            ServiceTo, DueDate, Lines, Net, VatRate, Vat, Gross, CancelsId, FileType, SentAt, SentTo, PaidOn, CreatedAt, CreatedBy, Version);
    }

    private const string SelectSql = """
        select d.id, d.run_id, d.kind, d.tenant_id, t.name as tenant_name, d.template_id, d.title, d.number, d.status, d.issue_date, d.service_from,
               d.service_to, d.due_date, d.lines::text as lines_json, d.net, d.vat_rate, d.vat, d.gross, d.cancels_id, d.file_type, d.sent_at, d.sent_to,
               d.paid_on, d.created_at, d.created_by, d.version
        from catalog.issued_document d join catalog.tenant t on t.id = d.tenant_id
        """;

    private static async Task<Row> LoadAsync(NpgsqlConnection conn, Guid id, CancellationToken ct, NpgsqlTransaction? tx = null, bool forUpdate = false) =>
        await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(SelectSql + " where d.id = @Id" + (forUpdate ? " for update of d" : ""),
            new { Id = id }, tx, cancellationToken: ct)) ?? throw ApiProblem.NotFound("Dokument nicht gefunden.");

    private static async Task<IReadOnlyList<IssuedDocument>> ListAsync(CatalogDb catalog, Guid? tenantId, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            SelectSql + " where (@Tenant::uuid is null or d.tenant_id = @Tenant) order by d.created_at desc, d.number desc nulls first limit 5000",
            new { Tenant = tenantId }, cancellationToken: ct));
        return rows.Select(r => r.ToDocument()).ToList();
    }

    private static bool IsInvoiceLike(string kind) => kind is DocumentKinds.Invoice or DocumentKinds.Cancellation;

    private static async Task<IResult> CreateSeriesAsync(CreateDocumentSeriesRequest r, HttpContext http, CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var (kind, _) = await PlatformTemplateEndpoints.LoadContentAsync(conn, r.TemplateId, ct);
        new Validator().Add(DocumentRules.Check(r, kind)).ThrowIfInvalid();

        var ids = r.TenantIds.Distinct().ToArray();
        var recipients = (await conn.QueryAsync<TenantBilling>(new CommandDefinition(PlatformCompanyEndpoints.BillingSql + " where t.id = any(@Ids)",
            new { Ids = ids }, cancellationToken: ct))).ToList();
        if (recipients.Count != ids.Length) throw ApiProblem.Invalid(nameof(r.TenantIds), "Mindestens ein ausgewählter Betrieb existiert nicht mehr.");

        var company = await PlatformCompanyEndpoints.LoadCompanyAsync(conn, ct);
        var vatRate = company.SmallBusiness ? 0m : company.VatRate;
        var prices = (await conn.QueryAsync<PackagePrice>(new CommandDefinition("""
            select id, valid_from, monthly_net, yearly_net, free_months, reduced_months, reduced_monthly_net, note, created_at, created_by
            from catalog.package_price
            """, cancellationToken: ct))).ToList();

        var drafts = new List<(TenantBilling Tenant, IReadOnlyList<DocumentLine> Lines)>();
        var problems = new List<string>();
        foreach (var tenant in recipients)
        {
            if (kind != DocumentKinds.Invoice)
            {
                drafts.Add((tenant, []));
                continue;
            }
            if (!tenant.IsComplete) { problems.Add($"{tenant.TenantName}: Rechnungsanschrift fehlt"); continue; }
            var lines = new List<DocumentLine>();
            foreach (var line in r.Lines!)
            {
                if (!line.UsePackagePrice) { lines.Add(line with { Description = line.Description.Trim() }); continue; }
                var unit = DocumentRules.PackageMonthlyNet(prices, tenant.SubscriptionStart, r.ServiceFrom!.Value);
                if (unit is null) { problems.Add($"{tenant.TenantName}: kein Paketpreis für den Leistungsmonat oder den Abo-Beginn"); break; }
                lines.Add(line with { Description = line.Description.Trim(), UnitNet = unit.Value });
            }
            drafts.Add((tenant, lines));
        }
        if (problems.Count > 0) throw ApiProblem.Invalid(nameof(r.TenantIds), string.Join("; ", problems.Distinct()) + ".");

        var runId = Guid.CreateVersion7();
        var by = http.User.DisplayName();
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var (tenant, lines) in drafts)
        {
            var (net, vat, gross) = DocumentRules.Totals(lines, vatRate);
            await conn.ExecuteAsync(new CommandDefinition("""
                insert into catalog.issued_document (id, run_id, kind, tenant_id, template_id, title, issue_date, service_from, service_to, lines,
                    net, vat_rate, vat, gross, created_by)
                values (@Id, @Run, @Kind, @Tenant, @Template, @Title, @IssueDate, @From, @To, cast(@Lines as jsonb), @Net, @Rate, @Vat, @Gross, @By)
                """, new
            {
                Id = Guid.CreateVersion7(), Run = runId, Kind = kind, Tenant = tenant.TenantId, Template = r.TemplateId, Title = r.Title.Trim(),
                r.IssueDate, From = kind == DocumentKinds.Invoice ? r.ServiceFrom : null, To = kind == DocumentKinds.Invoice ? r.ServiceTo : null,
                Lines = JsonSerializer.Serialize(lines, Json), Net = net, Rate = IsInvoiceLike(kind) ? vatRate : 0m, Vat = vat, Gross = gross, By = by,
            }, tx, cancellationToken: ct));
        }
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), by, "documents.series_created", null,
            new { RunId = runId, Kind = kind, r.TemplateId, Count = drafts.Count }, ct, tx);
        await tx.CommitAsync(ct);

        var created = await conn.QueryAsync<Row>(new CommandDefinition(SelectSql + " where d.run_id = @Run order by lower(t.name)", new { Run = runId },
            cancellationToken: ct));
        return Results.Created(PlatformRoutes.PlatformDocuments, created.Select(c => c.ToDocument()).ToList());
    }

    /// <summary>Platzhalterwerte und befülltes Word-Dokument für ein Dokument (Entwurf: ohne Nummer, mit „ENTWURF“).</summary>
    private static async Task<(Dictionary<string, string> Values, byte[] Docx)> ComposeAsync(NpgsqlConnection conn, Row doc, string? number,
        DateOnly? due, IReadOnlyList<DocumentLine> lines, (decimal Net, decimal Rate, decimal Vat, decimal Gross) totals, CompanyProfile company,
        CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        var billing = await conn.QuerySingleAsync<TenantBilling>(new CommandDefinition(PlatformCompanyEndpoints.BillingSql + " where t.id = @Id",
            new { Id = doc.TenantId }, tx, cancellationToken: ct));
        var cancels = doc.CancelsId is { } c
            ? await conn.ExecuteScalarAsync<string?>(new CommandDefinition("select number from catalog.issued_document where id = @Id", new { Id = c }, tx,
                cancellationToken: ct))
            : null;
        var recipient = new DocumentRecipient(billing.TenantName, billing.CustomerNumber, billing.RecipientName, billing.Street, billing.PostalCode,
            billing.City, billing.Country, billing.BillingEmail, billing.VatId);
        var facts = new DocumentFacts(doc.Kind, doc.Title, number, doc.IssueDate, doc.ServiceFrom, doc.ServiceTo, due, lines, totals.Net, totals.Rate,
            totals.Vat, totals.Gross, cancels);
        var values = DocumentValues.Build(company, recipient, facts);
        var (_, template) = await PlatformTemplateEndpoints.LoadContentAsync(conn, doc.TemplateId, ct);
        return (values, DocxFiller.Fill(template, values, DocumentValues.Lines(lines)));
    }

    /// <summary>
    /// Stellt einen Entwurf aus (in der laufenden Transaktion): Nummer, Fälligkeit, Steuer nach aktuellen Firmendaten, Datei. Die Nummer kommt
    /// aus einem Zähler je Jahr; scheitert etwas, rollt die Transaktion auch den Zähler zurück, es entstehen keine Lücken.
    /// </summary>
    private static async Task IssueInTxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, IDocumentConverter converter, string by, Guid userId,
        CancellationToken ct)
    {
        var doc = await LoadAsync(conn, id, ct, tx, forUpdate: true);
        if (doc.Status != DocumentStatus.Draft) throw ApiProblem.Conflict("Das Dokument ist bereits ausgestellt.");
        var company = await PlatformCompanyEndpoints.LoadCompanyAsync(conn, ct, tx);
        string? number = null;
        DateOnly? due = null;
        var lines = doc.Lines;
        var rate = 0m;
        if (IsInvoiceLike(doc.Kind))
        {
            if (!company.IsCompleteForInvoices)
                throw ApiProblem.Conflict("Die Firmendaten sind unvollständig (Name, Anschrift und Steuernummer oder USt-IdNr. sind Pflicht).");
            var billing = await conn.QuerySingleAsync<TenantBilling>(new CommandDefinition(PlatformCompanyEndpoints.BillingSql + " where t.id = @Id",
                new { Id = doc.TenantId }, tx, cancellationToken: ct));
            if (!billing.IsComplete) throw ApiProblem.Conflict($"Für {billing.TenantName} fehlt die Rechnungsanschrift.");
            var year = doc.IssueDate.Year;
            var counter = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                insert into catalog.document_counter (kind, year, last) values ('invoice', @Year, 1)
                on conflict (kind, year) do update set last = catalog.document_counter.last + 1
                returning last
                """, new { Year = year }, tx, cancellationToken: ct));
            number = $"{company.InvoicePrefix}-{year}-{counter:D5}";
            due = doc.Kind == DocumentKinds.Invoice ? doc.IssueDate.AddDays(company.PaymentTermDays) : null;
            // eine Stornorechnung übernimmt den Steuersatz der Rechnung, die sie aufhebt
            rate = doc.Kind == DocumentKinds.Cancellation ? doc.VatRate : company.SmallBusiness ? 0m : company.VatRate;
        }
        var (net, vat, gross) = DocumentRules.Totals(lines, rate);
        var (values, docx) = await ComposeAsync(conn, doc, number, due, lines, (net, rate, vat, gross), company, ct, tx);
        var (file, type) = await DocumentFiles.RenderAsync(converter, docx, ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            update catalog.issued_document set status = 'issued', number = @Number, due_date = @Due, net = @Net, vat_rate = @Rate, vat = @Vat,
                gross = @Gross, snapshot = cast(@Snapshot as jsonb), file = @File, file_type = @Type, issued_at = now(), version = version + 1
            where id = @Id
            """, new
        {
            Id = id, Number = number, Due = due, Net = net, Rate = rate, Vat = vat, Gross = gross, Snapshot = JsonSerializer.Serialize(values, Json),
            File = file, Type = type,
        }, tx, cancellationToken: ct));
        await PlatformAudit.WriteAsync(conn, userId, by, "document.issued", doc.TenantId, new { Id = id, doc.Kind, Number = number, Gross = gross }, ct, tx);
    }

    private static async Task<IReadOnlyList<DocumentBatchItem>> IssueAsync(DocumentBatchRequest r, HttpContext http, CatalogDb catalog,
        IDocumentConverter converter, ILoggerFactory logs, CancellationToken ct)
    {
        CheckBatch(r);
        var results = new List<DocumentBatchItem>();
        await using var conn = await catalog.OpenAsync(ct);
        // je Dokument eine eigene Transaktion: ein Fehler bei einem Betrieb hält die anderen nicht auf
        foreach (var id in r.Ids.Distinct())
        {
            try
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await IssueInTxAsync(conn, tx, id, converter, http.User.DisplayName(), http.User.UserId(), ct);
                await tx.CommitAsync(ct);
                results.Add(new DocumentBatchItem(id, true, null, (await LoadAsync(conn, id, ct)).ToDocument()));
            }
            catch (ApiProblem p)
            {
                results.Add(new DocumentBatchItem(id, false, p.Message, null));
            }
            catch (InvalidOperationException e)
            {
                logs.CreateLogger(typeof(PlatformDocumentEndpoints)).LogWarning(e, "Dokument {Id} ließ sich nicht ausstellen.", id);
                results.Add(new DocumentBatchItem(id, false, e.Message, null));
            }
        }
        return results;
    }

    private static void CheckBatch(DocumentBatchRequest r) => new Validator()
        .Check(r.Ids is { Count: > 0 and <= DocumentRules.MaxTenantsPerSeries }, "Ids", "Bitte 1 bis 500 Dokumente auswählen.").ThrowIfInvalid();

    private static async Task<IReadOnlyList<DocumentBatchItem>> SendAsync(DocumentBatchRequest r, HttpContext http, CatalogDb catalog, IPlatformMailer mailer,
        ILoggerFactory logs, CancellationToken ct)
    {
        CheckBatch(r);
        if (!mailer.IsConfigured) throw ApiProblem.Unavailable("Der E-Mail-Versand ist nicht eingerichtet (Smtp__Host und Smtp__From).");
        var results = new List<DocumentBatchItem>();
        await using var conn = await catalog.OpenAsync(ct);
        var company = await PlatformCompanyEndpoints.LoadCompanyAsync(conn, ct);
        foreach (var id in r.Ids.Distinct())
        {
            try
            {
                var doc = await LoadAsync(conn, id, ct);
                if (doc.Status is DocumentStatus.Draft or DocumentStatus.Cancelled)
                    throw ApiProblem.Conflict(doc.Status == DocumentStatus.Draft ? "Bitte zuerst ausstellen." : "Stornierte Rechnungen werden nicht versendet.");
                var to = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "select billing_email from (" + PlatformCompanyEndpoints.BillingSql + " where t.id = @Id) b", new { Id = doc.TenantId }, cancellationToken: ct));
                if (string.IsNullOrWhiteSpace(to) || !Validation.IsEmail(to)) throw ApiProblem.Conflict($"Für {doc.TenantName} fehlt die E-Mail für Rechnungen.");
                var file = await conn.QuerySingleAsync<(byte[] File, string Type)>(new CommandDefinition(
                    "select file, file_type from catalog.issued_document where id = @Id", new { Id = id }, cancellationToken: ct));
                var described = DocumentFiles.Describe(file.File, file.Type, FileBaseName(doc));
                var label = doc.Number is null ? doc.Title : $"{doc.Title} {doc.Number}";
                var sender = string.IsNullOrWhiteSpace(company.LegalName) ? "KaiZent" : company.LegalName;
                var body = $"Guten Tag,\n\nim Anhang erhalten Sie {label} von {sender}.\n\nBei Fragen antworten Sie gern auf diese E-Mail.\n\n"
                    + $"Mit freundlichen Grüßen\n{sender}";
                await mailer.SendAsync(to, $"{label} – {sender}", body, new MailAttachment(described.FileName, described.ContentType, file.File),
                    Validation.IsEmail(company.Email) ? company.Email : null, ct);

                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(new CommandDefinition("""
                    update catalog.issued_document set sent_at = now(), sent_to = @To,
                        status = case when status = 'issued' then 'sent' else status end, version = version + 1
                    where id = @Id
                    """, new { Id = id, To = to }, tx, cancellationToken: ct));
                await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "document.sent", doc.TenantId,
                    new { Id = id, doc.Number, To = to }, ct, tx);
                await tx.CommitAsync(ct);
                results.Add(new DocumentBatchItem(id, true, null, (await LoadAsync(conn, id, ct)).ToDocument()));
            }
            catch (ApiProblem p)
            {
                results.Add(new DocumentBatchItem(id, false, p.Message, null));
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException)
            {
                logs.CreateLogger(typeof(PlatformDocumentEndpoints)).LogWarning(e, "Dokument {Id} ließ sich nicht versenden.", id);
                results.Add(new DocumentBatchItem(id, false, e.Message, null));
            }
        }
        return results;
    }

    private static string FileBaseName(Row doc) => doc.Number is null ? $"{doc.Title} {doc.TenantName}" : $"{doc.Title} {doc.Number}";

    /// <summary>Gespeicherte Datei eines ausgestellten Dokuments; bei Entwürfen eine Vorschau mit „ENTWURF“ statt Nummer.</summary>
    private static async Task<DocumentFile> FileAsync(Guid id, CatalogDb catalog, IDocumentConverter converter, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var doc = await LoadAsync(conn, id, ct);
        if (doc.Status != DocumentStatus.Draft)
        {
            var file = await conn.QuerySingleAsync<(byte[] File, string Type)>(new CommandDefinition(
                "select file, file_type from catalog.issued_document where id = @Id", new { Id = id }, cancellationToken: ct));
            return DocumentFiles.Describe(file.File, file.Type, FileBaseName(doc));
        }
        var company = await PlatformCompanyEndpoints.LoadCompanyAsync(conn, ct);
        var rate = IsInvoiceLike(doc.Kind) && !company.SmallBusiness ? company.VatRate : 0m;
        var (net, vat, gross) = DocumentRules.Totals(doc.Lines, rate);
        DateOnly? due = doc.Kind == DocumentKinds.Invoice ? doc.IssueDate.AddDays(company.PaymentTermDays) : null;
        var (_, docx) = await ComposeAsync(conn, doc, null, due, doc.Lines, (net, rate, vat, gross), company, ct);
        return await DocumentFiles.ToFileAsync(converter, docx, "Entwurf " + FileBaseName(doc), ct);
    }

    private static async Task<IssuedDocument> MarkPaidAsync(Guid id, MarkPaidRequest r, HttpContext http, CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var doc = await LoadAsync(conn, id, ct);
        if (doc.Kind != DocumentKinds.Invoice || doc.Status is not (DocumentStatus.Issued or DocumentStatus.Sent))
            throw ApiProblem.Conflict("Nur ausgestellte, nicht stornierte Rechnungen lassen sich als bezahlt markieren.");
        if (r.PaidOn < doc.IssueDate) throw ApiProblem.Invalid(nameof(r.PaidOn), "Das Zahlungsdatum liegt vor dem Rechnungsdatum.");
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "update catalog.issued_document set status = 'paid', paid_on = @PaidOn, version = version + 1 where id = @Id and version = @Version",
            new { Id = id, r.PaidOn, r.Version }, tx, cancellationToken: ct));
        if (n == 0) throw ApiProblem.Stale();
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "document.paid", doc.TenantId, new { Id = id, doc.Number, r.PaidOn },
            ct, tx);
        await tx.CommitAsync(ct);
        return (await LoadAsync(conn, id, ct)).ToDocument();
    }

    /// <summary>Storniert eine ausgestellte Rechnung: neue Stornorechnung mit negativen Beträgen (eigene Nummer), die Rechnung wird „storniert“.</summary>
    private static async Task<IssuedDocument> CancelAsync(Guid id, HttpContext http, CatalogDb catalog, IDocumentConverter converter, TimeProvider time,
        CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var doc = await LoadAsync(conn, id, ct, tx, forUpdate: true);
        if (doc.Kind != DocumentKinds.Invoice || doc.Status is DocumentStatus.Draft or DocumentStatus.Cancelled)
            throw ApiProblem.Conflict("Nur ausgestellte, nicht stornierte Rechnungen lassen sich stornieren. Entwürfe einfach löschen.");
        var stornoId = Guid.CreateVersion7();
        var lines = doc.Lines.Select(l => l with { UnitNet = -l.UnitNet, UsePackagePrice = false }).ToList();
        var (net, vat, gross) = DocumentRules.Totals(lines, doc.VatRate);
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into catalog.issued_document (id, run_id, kind, tenant_id, template_id, title, issue_date, service_from, service_to, lines,
                net, vat_rate, vat, gross, cancels_id, created_by)
            values (@Id, @Run, 'cancellation', @Tenant, @Template, 'Stornorechnung', @IssueDate, @From, @To, cast(@Lines as jsonb), @Net, @Rate, @Vat,
                @Gross, @Cancels, @By)
            """, new
        {
            Id = stornoId, Run = Guid.CreateVersion7(), Tenant = doc.TenantId, Template = doc.TemplateId, IssueDate = PlatformClock.Today(time),
            From = doc.ServiceFrom, To = doc.ServiceTo, Lines = JsonSerializer.Serialize(lines, Json), Net = net, Rate = doc.VatRate, Vat = vat,
            Gross = gross, Cancels = id, By = http.User.DisplayName(),
        }, tx, cancellationToken: ct));
        await IssueInTxAsync(conn, tx, stornoId, converter, http.User.DisplayName(), http.User.UserId(), ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "update catalog.issued_document set status = 'cancelled', version = version + 1 where id = @Id", new { Id = id }, tx, cancellationToken: ct));
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "document.cancelled", doc.TenantId,
            new { Id = id, doc.Number, Cancellation = stornoId }, ct, tx);
        await tx.CommitAsync(ct);
        return (await LoadAsync(conn, stornoId, ct)).ToDocument();
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var doc = await LoadAsync(conn, id, ct, tx, forUpdate: true);
        if (doc.Status != DocumentStatus.Draft) throw ApiProblem.Conflict("Ausgestellte Dokumente bleiben erhalten; Rechnungen bitte stornieren.");
        await conn.ExecuteAsync(new CommandDefinition("delete from catalog.issued_document where id = @Id", new { Id = id }, tx, cancellationToken: ct));
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "document.deleted", doc.TenantId, new { Id = id, doc.Title }, ct, tx);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
