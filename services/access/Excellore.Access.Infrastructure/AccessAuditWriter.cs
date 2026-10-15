using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Excellore.Access.Infrastructure;

internal static class AccessAuditWriter
{
    // Shared by every Access writer so two transactions cannot fork a scope's hash chain.
    internal const long AuditLock = 0x4558434154414C4F;

    internal static async Task WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid? tenantId, Guid actorSub, string actorType, string action, string targetType,
        string targetId, string reason, CancellationToken cancellationToken,
        string? beforeJson = null, string? afterJson = null, string? acr = null)
    {
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            command.Parameters.AddWithValue("key", AuditLock);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        byte[]? previous = null;
        DateTimeOffset? previousAt = null;
        await using (var command = new NpgsqlCommand("""
            SELECT occurred_at, hash FROM audit_events WHERE tenant_id IS NOT DISTINCT FROM @tenant
            ORDER BY occurred_at DESC, id DESC LIMIT 1
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", NpgsqlTypes.NpgsqlDbType.Uuid,
                (object?)tenantId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                previousAt = reader.GetFieldValue<DateTimeOffset>(0);
                previous = reader.GetFieldValue<byte[]>(1);
            }
        }

        string? before;
        string? after;
        await using (var canonical = new NpgsqlCommand(
            "SELECT @before::jsonb::text, @after::jsonb::text", connection, transaction))
        {
            canonical.Parameters.AddWithValue("before", NpgsqlTypes.NpgsqlDbType.Text,
                (object?)beforeJson ?? DBNull.Value);
            canonical.Parameters.AddWithValue("after", NpgsqlTypes.NpgsqlDbType.Text,
                (object?)afterJson ?? DBNull.Value);
            await using var reader = await canonical.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            before = reader.IsDBNull(0) ? null : reader.GetString(0);
            after = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var occurredAt = now.AddTicks(-(now.Ticks % 10)); // PostgreSQL timestamp precision.
        if (previousAt is not null && occurredAt <= previousAt.Value)
            occurredAt = previousAt.Value.AddTicks(10);
        var record = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new AuditHashRecord(
            2, id, tenantId, occurredAt, actorSub, actorType, action, targetType, targetId,
            reason, before, after, acr)));
        var input = new byte[(previous?.Length ?? 0) + record.Length];
        previous?.CopyTo(input, 0);
        record.CopyTo(input, previous?.Length ?? 0);
        var hash = SHA256.HashData(input);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO audit_events
              (id, tenant_id, occurred_at, actor_sub, actor_type, action, target_type, target_id,
               reason, before, after, acr, prev_hash, hash, hash_version)
            VALUES (@id, @tenant, @occurred, @actor, @actor_type, @action, @target_type, @target_id,
                    @reason, @before, @after, @acr, @previous, @hash, 2)
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
        insert.Parameters.AddWithValue("before", NpgsqlTypes.NpgsqlDbType.Jsonb,
            (object?)before ?? DBNull.Value);
        insert.Parameters.AddWithValue("after", NpgsqlTypes.NpgsqlDbType.Jsonb,
            (object?)after ?? DBNull.Value);
        insert.Parameters.AddWithValue("acr", NpgsqlTypes.NpgsqlDbType.Text,
            (object?)acr ?? DBNull.Value);
        insert.Parameters.AddWithValue("previous", NpgsqlTypes.NpgsqlDbType.Bytea, (object?)previous ?? DBNull.Value);
        insert.Parameters.AddWithValue("hash", hash);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    internal sealed record AuditHashRecord(int Version, Guid Id, Guid? TenantId,
        DateTimeOffset OccurredAt, Guid ActorSub, string ActorType, string Action,
        string TargetType, string TargetId, string Reason, string? Before, string? After,
        string? Acr);
}
