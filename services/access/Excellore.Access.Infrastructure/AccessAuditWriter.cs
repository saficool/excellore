using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Excellore.Access.Infrastructure;

internal static class AccessAuditWriter
{
    // Shared by every Access writer so two transactions cannot fork a scope's hash chain.
    internal const long AuditLock = 0x4558434154414C4F;

    internal static async Task WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid? tenantId, Guid actorSub, string actorType, string action, string targetType,
        string targetId, string reason, CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            command.Parameters.AddWithValue("key", AuditLock);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        byte[]? previous;
        await using (var command = new NpgsqlCommand("""
            SELECT hash FROM audit_events WHERE tenant_id IS NOT DISTINCT FROM @tenant
            ORDER BY occurred_at DESC, id DESC LIMIT 1
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", NpgsqlTypes.NpgsqlDbType.Uuid,
                (object?)tenantId ?? DBNull.Value);
            previous = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        }

        var id = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        var record = Encoding.UTF8.GetBytes($"{id:D}|{occurredAt:O}|{actorSub:D}|{action}|{targetType}|{targetId}|{reason}");
        var input = new byte[(previous?.Length ?? 0) + record.Length];
        previous?.CopyTo(input, 0);
        record.CopyTo(input, previous?.Length ?? 0);
        var hash = SHA256.HashData(input);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO audit_events
              (id, tenant_id, occurred_at, actor_sub, actor_type, action, target_type, target_id, reason, prev_hash, hash)
            VALUES (@id, @tenant, @occurred, @actor, @actor_type, @action, @target_type, @target_id, @reason, @previous, @hash)
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("tenant", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)tenantId ?? DBNull.Value);
        insert.Parameters.AddWithValue("occurred", occurredAt);
        insert.Parameters.AddWithValue("actor", actorSub);
        insert.Parameters.AddWithValue("actor_type", actorType);
        insert.Parameters.AddWithValue("action", action);
        insert.Parameters.AddWithValue("target_type", targetType);
        insert.Parameters.AddWithValue("target_id", targetId);
        insert.Parameters.AddWithValue("reason", reason);
        insert.Parameters.AddWithValue("previous", NpgsqlTypes.NpgsqlDbType.Bytea, (object?)previous ?? DBNull.Value);
        insert.Parameters.AddWithValue("hash", hash);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }
}
