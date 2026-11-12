using Dapper;
using KaiZent.Platform.Api;
using KaiZent.Platform.Api.Infrastructure;
using KaiZent.Platform.Data;

var builder = WebApplication.CreateBuilder(args);
builder.AddKaiZentPlatform(new PlatformProduct("KaiZent Beispiel", "kzsample_t_",
    new MigrationSet("sample", typeof(SampleNote).Assembly, "migrations/SAMPLE/"), [SampleModules.Notes, SampleModules.Extra]));

var app = builder.Build();
await app.InitializeKaiZentPlatformAsync();
var api = app.MapKaiZentPlatform();

// Fachrouten eines Produkts: nur über TenantContext an die Daten des eigenen Mandanten, jede Änderung ins Protokoll
var tenant = api.MapTenantApi("/api/sample");
tenant.MapGet("/notes", async (TenantContext ctx, CancellationToken ct) =>
{
    ctx.RequireModule(SampleModules.Notes);
    await using var db = await ctx.OpenAsync(ct);
    return (await db.QueryAsync<SampleNote>(new CommandDefinition("select id, text from sample.note order by created_at", cancellationToken: ct))).ToList();
});
tenant.MapPost("/notes", async (SampleNote note, TenantContext ctx, CancellationToken ct) =>
{
    ctx.RequireModule(SampleModules.Notes);
    var id = Guid.CreateVersion7();
    await using var db = await ctx.OpenAsync(ct);
    await db.ExecuteAsync(new CommandDefinition("insert into sample.note (id, text) values (@Id, @Text)", new { Id = id, note.Text }, cancellationToken: ct));
    await ctx.AuditAsync(db, "note.created", "note", id.ToString(), ct: ct);
    return Results.Ok(note with { Id = id });
});

app.Run();

public sealed record SampleNote(Guid Id, string Text);

public static class SampleModules
{
    public const string Notes = "notes";
    public const string Extra = "extra";
}

/// <summary>Für WebApplicationFactory in den Tests.</summary>
public partial class Program;
