using System.Text.Json;
using Dapper;
using Npgsql;

namespace KaiZent.Platform.Data;

/// <summary>Protokoll der Mandanten-Datenbank. Die API schreibt mit dem angemeldeten Benutzer, Hintergrunddienste als <see cref="SystemUserName"/>.</summary>
public static class AuditLog
{
    /// <summary>Kennung für Einträge, die kein Mensch, sondern ein Hintergrunddienst geschrieben hat.</summary>
    public static readonly Guid SystemUserId = Guid.Empty;
    public const string SystemUserName = "System";

    public static Task WriteAsync(NpgsqlConnection tenantDb, Guid userId, string userName, string action, string entity, string? entityId,
        object? details = null, NpgsqlTransaction? tx = null, CancellationToken ct = default) =>
        tenantDb.ExecuteAsync(new CommandDefinition("""
            insert into audit.audit_log (user_id, user_name, action, entity, entity_id, details)
            values (@UserId, @UserName, @Action, @Entity, @EntityId, cast(@Details as jsonb))
            """,
            new
            {
                UserId = userId,
                UserName = userName,
                Action = action,
                Entity = entity,
                EntityId = entityId,
                Details = details is null ? null : JsonSerializer.Serialize(details),
            }, tx, cancellationToken: ct));
}
