using System.Text;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Access.Infrastructure;

/// <summary>Builds one private, bounded audit export in the current tenant context.</summary>
public sealed class AuditExportBuilder(NpgsqlDataSource database, ITenantIdAccessor tenant,
    IAuditExportObjectStore objects)
{
    private const int MaxBytes = 64 * 1024 * 1024;

    public async Task<bool> ProcessOneAsync(Guid machineActorId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.TenantId;
        if (machineActorId == Guid.Empty)
            throw new ArgumentException("Machine actor is required.", nameof(machineActorId));
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction))
        {
            scope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }

        Guid? exportId;
        await using (var pending = new NpgsqlCommand("""
            SELECT id FROM audit_exports
            WHERE tenant_id=@tenant AND status='pending'
            ORDER BY created_at,id LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection, transaction))
        {
            pending.Parameters.AddWithValue("tenant", tenantId);
            exportId = await pending.ExecuteScalarAsync(cancellationToken) as Guid?;
        }
        if (exportId is null) return false;

        using var buffer = new MemoryStream();
        var tooLarge = false;
        await using (var rows = new NpgsqlCommand("""
            SELECT row_to_json(a)::text FROM (
              SELECT id,tenant_id,occurred_at,actor_sub,actor_type,action,target_type,
                target_id,before,after,reason,acr,source_ip,support_grant_id,
                correlation_id,encode(prev_hash,'hex') AS prev_hash,
                encode(hash,'hex') AS hash,hash_version
              FROM audit_events WHERE tenant_id=@tenant ORDER BY occurred_at,id
            ) a
            """, connection, transaction))
        {
            rows.Parameters.AddWithValue("tenant", tenantId);
            await using var reader = await rows.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var line = reader.GetString(0);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                if (buffer.Length + bytes.Length > MaxBytes)
                {
                    tooLarge = true;
                    break;
                }
                await buffer.WriteAsync(bytes, cancellationToken);
            }
        }

        if (tooLarge)
        {
            await using var failed = new NpgsqlCommand("""
                UPDATE audit_exports SET status='failed'
                WHERE tenant_id=@tenant AND id=@id AND status='pending'
                """, connection, transaction);
            failed.Parameters.AddWithValue("tenant", tenantId);
            failed.Parameters.AddWithValue("id", exportId.Value);
            await failed.ExecuteNonQueryAsync(cancellationToken);
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, machineActorId,
                "service", "access.audit.export-failed", "audit-export", exportId.Value.ToString("D"),
                "size-limit", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var key = $"audit-exports/{exportId.Value:D}.jsonl";
        await objects.PutAsync(key, buffer.ToArray(), cancellationToken);
        await using (var ready = new NpgsqlCommand("""
            UPDATE audit_exports SET status='ready',object_key=@key,ready_at=now()
            WHERE tenant_id=@tenant AND id=@id AND status='pending'
            """, connection, transaction))
        {
            ready.Parameters.AddWithValue("tenant", tenantId);
            ready.Parameters.AddWithValue("id", exportId.Value);
            ready.Parameters.AddWithValue("key", key);
            if (await ready.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Audit export state changed unexpectedly.");
        }
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, machineActorId,
            "service", "access.audit.export-ready", "audit-export", exportId.Value.ToString("D"),
            "worker-complete", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
