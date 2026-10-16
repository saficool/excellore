using System.Text.Json;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Access.Infrastructure;

/// <summary>Finalizes Access erasure only after Profile has confirmed its own erasure.</summary>
public sealed class TenantErasureFinalizer(NpgsqlDataSource database, ITenantIdAccessor tenant,
    IAuditExportObjectStore objects)
{
    // Child rows precede their parents. These are constants, never user input.
    private static readonly string[] TenantTables =
    [
        "group_role_assignments", "group_members", "role_assignments",
        "ownership_transfers", "role_assignment_batches", "access_requests",
        "invitations", "audit_exports", "tenant_exports", "tenant_settings", "tenant_apps",
        "support_grants", "groups", "tenant_members", "role_permissions",
        "roles", "user_tenant_index"
    ];

    public async Task<bool> FinalizeAsync(Guid machineActorId,
        CancellationToken cancellationToken = default)
    {
        if (machineActorId == Guid.Empty)
            throw new ArgumentException("Machine actor is required.", nameof(machineActorId));
        var tenantId = tenant.TenantId;
        var keys = new List<string>();
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await SetTenantAsync(connection, transaction, tenantId, cancellationToken);
            await using (var ready = new NpgsqlCommand("""
                SELECT 1 FROM tenants t JOIN tenant_tombstones x ON x.tenant_id=t.id
                WHERE t.id=@tenant AND t.status='pending_deletion'
                  AND t.deletion_started_at IS NOT NULL
                  AND x.profile_confirmed_at IS NOT NULL
                  AND x.completed_at IS NULL
                """, connection, transaction))
            {
                ready.Parameters.AddWithValue("tenant", tenantId);
                if (await ready.ExecuteScalarAsync(cancellationToken) is null)
                    return false;
            }
            await using (var exports = new NpgsqlCommand("""
                SELECT object_key FROM audit_exports
                WHERE tenant_id=@tenant AND object_key IS NOT NULL
                """, connection, transaction))
            {
                exports.Parameters.AddWithValue("tenant", tenantId);
                await using var reader = await exports.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    keys.Add(reader.GetString(0));
            }
            await transaction.CommitAsync(cancellationToken);
        }

        // Object deletion is idempotent. A failure leaves the database pending for retry.
        foreach (var key in keys)
            await objects.DeleteAsync(key, cancellationToken);

        await using var finalizeConnection = await database.OpenConnectionAsync(cancellationToken);
        await using var finalizeTransaction = await finalizeConnection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(finalizeConnection, finalizeTransaction, tenantId, cancellationToken);
        await using (var lockTenant = new NpgsqlCommand("""
            SELECT 1 FROM tenants t JOIN tenant_tombstones x ON x.tenant_id=t.id
            WHERE t.id=@tenant AND t.status='pending_deletion'
              AND t.deletion_started_at IS NOT NULL
              AND x.profile_confirmed_at IS NOT NULL
              AND x.completed_at IS NULL
            FOR UPDATE OF t,x
            """, finalizeConnection, finalizeTransaction))
        {
            lockTenant.Parameters.AddWithValue("tenant", tenantId);
            if (await lockTenant.ExecuteScalarAsync(cancellationToken) is null)
                return false;
        }
        await AccessAuditWriter.WriteAsync(finalizeConnection, finalizeTransaction, tenantId,
            machineActorId, "service", "access.tenant.erasure-finalized", "tenant",
            tenantId.ToString("D"), "Services confirmed erasure", cancellationToken);
        await using (var marker = new NpgsqlCommand("""
            UPDATE tenant_tombstones SET access_confirmed_at=now(),completed_at=now()
            WHERE tenant_id=@tenant AND profile_confirmed_at IS NOT NULL
            """, finalizeConnection, finalizeTransaction))
        {
            marker.Parameters.AddWithValue("tenant", tenantId);
            if (await marker.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Tenant erasure receipt unavailable.");
        }
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('app.erasure','true',true)", finalizeConnection, finalizeTransaction))
            await scope.ExecuteNonQueryAsync(cancellationToken);

        await ExecuteTenantSqlAsync(finalizeConnection, finalizeTransaction, tenantId,
            "INSERT INTO audit_events_archive SELECT * FROM audit_events WHERE tenant_id=@tenant",
            cancellationToken);
        await ExecuteTenantSqlAsync(finalizeConnection, finalizeTransaction, tenantId,
            "DELETE FROM audit_events WHERE tenant_id=@tenant", cancellationToken);
        foreach (var table in TenantTables)
            await ExecuteTenantSqlAsync(finalizeConnection, finalizeTransaction, tenantId,
                $"DELETE FROM {table} WHERE tenant_id=@tenant", cancellationToken);
        if (await ExecuteTenantSqlAsync(finalizeConnection, finalizeTransaction, tenantId,
            "DELETE FROM tenants WHERE id=@tenant", cancellationToken) != 1)
            throw new InvalidOperationException("Tenant registry row unavailable.");

        var eventId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0", id = eventId, type = "access.erasure.confirmed.v1",
            source = "excellore/access", time = DateTimeOffset.UtcNow,
            tenantid = tenantId, correlationid = Guid.NewGuid(),
            actor = new { sub = machineActorId.ToString("D"), type = "machine" },
            data = new { scope = "tenant", tenantId, service = "access" }
        });
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO outbox (id,type,tenant_id,payload)
            VALUES (@id,'access.erasure.confirmed.v1',@tenant,@payload::jsonb)
            """, finalizeConnection, finalizeTransaction))
        {
            outbox.Parameters.AddWithValue("id", eventId);
            outbox.Parameters.AddWithValue("tenant", tenantId);
            outbox.Parameters.AddWithValue("payload", payload);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        await finalizeTransaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction);
        scope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
        await scope.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ExecuteTenantSqlAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
