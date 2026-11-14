using Dapper;
using KaiZent.Platform.Api.Documents;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Contracts.Billing;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace KaiZent.Platform.Api.Features;

/// <summary>Plattform-Administration: Vorlagen aus dem Dokumentdesigner (Word-Format mit Platzhaltern) für Rechnungen und Serienbriefe.</summary>
public static class PlatformTemplateEndpoints
{
    public static void MapPlatformTemplates(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization(Policies.Platform).AddEndpointFilter<AccessGuard>();
        group.MapGet(PlatformRoutes.PlatformTemplates, ListAsync).WithName("ListDocumentTemplates");
        group.MapGet(PlatformRoutes.PlatformTemplatePlaceholders, (string? kind) => DocumentPlaceholders.For(kind ?? DocumentKinds.Invoice))
            .WithName("ListDocumentPlaceholders");
        group.MapPost(PlatformRoutes.PlatformTemplates, CreateAsync).WithName("CreateDocumentTemplate");
        group.MapGet(PlatformRoutes.PlatformTemplates + "/{id:guid}", GetAsync).WithName("GetDocumentTemplate");
        group.MapPut(PlatformRoutes.PlatformTemplates + "/{id:guid}", UpdateAsync).WithName("UpdateDocumentTemplate");
        group.MapDelete(PlatformRoutes.PlatformTemplates + "/{id:guid}", DeleteAsync).WithName("DeleteDocumentTemplate");
        group.MapGet(PlatformRoutes.PlatformTemplates + "/{id:guid}/preview", PreviewAsync).WithName("PreviewDocumentTemplate");
    }

    private sealed record Row(Guid Id, string Kind, string Name, string? Description, byte[] Content, DateTimeOffset UpdatedAt, string UpdatedBy, int Version);

    private const string SelectSql = "select id, kind, name, description, content, updated_at, updated_by, version from catalog.document_template";

    private static async Task<IReadOnlyList<DocumentTemplateSummary>> ListAsync(CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(SelectSql + " order by kind, lower(name)", cancellationToken: ct));
        return rows.Select(r => new DocumentTemplateSummary(r.Id, r.Kind, r.Name, r.Description, DocxFiller.ReadPlaceholders(r.Content) ?? [],
            r.UpdatedAt, r.UpdatedBy, r.Version)).ToList();
    }

    internal static async Task<(string Kind, byte[] Content)> LoadContentAsync(Npgsql.NpgsqlConnection conn, Guid id, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<(string, byte[])>(new CommandDefinition(
            "select kind, content from catalog.document_template where id = @Id", new { Id = id }, cancellationToken: ct)) is { Item2: not null } found
            ? found : throw ApiProblem.NotFound("Vorlage nicht gefunden.");

    private static async Task<DocumentTemplate> GetAsync(Guid id, CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var r = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(SelectSql + " where id = @Id", new { Id = id }, cancellationToken: ct))
            ?? throw ApiProblem.NotFound("Vorlage nicht gefunden.");
        return new DocumentTemplate(r.Id, r.Kind, r.Name, r.Description, Convert.ToBase64String(r.Content), r.Version);
    }

    /// <summary>Prüft Base64 und Word-Format; null: kein Inhalt angegeben.</summary>
    private static byte[]? ReadContent(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw ApiProblem.Invalid("ContentBase64", "Die Datei ist beschädigt."); }
        if (bytes.Length > DocumentRules.MaxTemplateBytes)
            throw ApiProblem.Invalid("ContentBase64", $"Die Vorlage ist zu groß (höchstens {DocumentRules.MaxTemplateBytes / 1024 / 1024} MB).");
        if (DocxFiller.ReadPlaceholders(bytes) is null) throw ApiProblem.Invalid("ContentBase64", "Die Datei ist kein Word-Dokument (.docx).");
        return bytes;
    }

    private static void CheckName(string? name, string? description) => new Validator()
        .Check(Validation.IsName(name), "Name", "Bitte einen Namen angeben (höchstens 200 Zeichen).")
        .Check(description is null || description.Length <= DocumentRules.MaxTextLength, "Description", $"Höchstens {DocumentRules.MaxTextLength} Zeichen.")
        .ThrowIfInvalid();

    private static async Task<IResult> CreateAsync(CreateDocumentTemplateRequest r, HttpContext http, CatalogDb catalog, CancellationToken ct)
    {
        CheckName(r.Name, r.Description);
        if (!DocumentKinds.TemplateKinds.Contains(r.Kind)) throw ApiProblem.Invalid(nameof(r.Kind), "Unbekannte Vorlagenart.");
        var content = ReadContent(r.ContentBase64) ?? DefaultTemplates.For(r.Kind);
        var id = Guid.CreateVersion7();
        await using var conn = await catalog.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into catalog.document_template (id, kind, name, description, content, updated_by) values (@Id, @Kind, @Name, @Description, @Content, @By)
            """, new { Id = id, r.Kind, Name = r.Name.Trim(), Description = Clean(r.Description), Content = content, By = http.User.DisplayName() },
            tx, cancellationToken: ct));
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "template.created", null, new { Id = id, r.Kind, r.Name }, ct, tx);
        await tx.CommitAsync(ct);
        return Results.Created(PlatformRoutes.PlatformTemplate(id), await GetAsync(id, catalog, ct));
    }

    private static async Task<DocumentTemplate> UpdateAsync(Guid id, UpdateDocumentTemplateRequest r, HttpContext http, CatalogDb catalog, CancellationToken ct)
    {
        CheckName(r.Name, r.Description);
        var content = ReadContent(r.ContentBase64);
        await using var conn = await catalog.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            update catalog.document_template set name = @Name, description = @Description, content = coalesce(@Content, content),
                version = version + 1, updated_at = now(), updated_by = @By
            where id = @Id and version = @Version
            """, new { Id = id, Name = r.Name.Trim(), Description = Clean(r.Description), Content = content, By = http.User.DisplayName(), r.Version },
            tx, cancellationToken: ct));
        if (n == 0)
        {
            await LoadContentAsync(conn, id, ct);   // 404, wenn es die Vorlage nicht gibt
            throw ApiProblem.Stale();
        }
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "template.updated", null,
            new { Id = id, r.Name, ContentChanged = content is not null }, ct, tx);
        await tx.CommitAsync(ct);
        return await GetAsync(id, catalog, ct);
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, CatalogDb catalog, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        await LoadContentAsync(conn, id, ct);
        if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition("select exists (select 1 from catalog.issued_document where template_id = @Id)",
                new { Id = id }, cancellationToken: ct)))
            throw ApiProblem.Conflict("Mit dieser Vorlage wurden schon Dokumente erzeugt; sie bleibt deshalb erhalten.");
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("delete from catalog.document_template where id = @Id", new { Id = id }, tx, cancellationToken: ct));
        await PlatformAudit.WriteAsync(conn, http.User.UserId(), http.User.DisplayName(), "template.deleted", null, new { Id = id }, ct, tx);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Vorlage mit Beispielwerten und den aktuellen Firmendaten, als PDF (falls eingerichtet) oder Word.</summary>
    private static async Task<DocumentFile> PreviewAsync(Guid id, CatalogDb catalog, IDocumentConverter converter, TimeProvider time, CancellationToken ct)
    {
        await using var conn = await catalog.OpenAsync(ct);
        var (kind, content) = await LoadContentAsync(conn, id, ct);
        var company = await PlatformCompanyEndpoints.LoadCompanyAsync(conn, ct);
        var (recipient, facts) = DocumentValues.Sample(kind, PlatformClock.Today(time));
        var docx = DocxFiller.Fill(content, DocumentValues.Build(company, recipient, facts), DocumentValues.Lines(facts.Lines));
        return await DocumentFiles.ToFileAsync(converter, docx, "Vorschau", ct);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>Datei für den Download: PDF, wenn ein Umwandler eingerichtet ist, sonst Word.</summary>
internal static class DocumentFiles
{
    public const string PdfType = "application/pdf";
    public const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public static async Task<(byte[] Bytes, string Type)> RenderAsync(IDocumentConverter converter, byte[] docx, CancellationToken ct) =>
        await converter.ToPdfAsync(docx, ct) is { } pdf ? (pdf, "pdf") : (docx, "docx");

    public static async Task<DocumentFile> ToFileAsync(IDocumentConverter converter, byte[] docx, string baseName, CancellationToken ct)
    {
        var (bytes, type) = await RenderAsync(converter, docx, ct);
        return Describe(bytes, type, baseName);
    }

    public static DocumentFile Describe(byte[] bytes, string type, string baseName) =>
        new(SafeName(baseName) + "." + type, type == "pdf" ? PdfType : DocxType, Convert.ToBase64String(bytes));

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "Dokument" : clean.Length > 120 ? clean[..120] : clean;
    }
}
