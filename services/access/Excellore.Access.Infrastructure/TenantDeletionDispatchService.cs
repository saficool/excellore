using System.Text.Json;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Access.Infrastructure;

/// <summary>Dispatches a due deletion once, in the same transaction as its outbox event.</summary>
public sealed class TenantDeletionDispatchService(NpgsqlDataSource database, ITenantIdAccessor tenant)
{
    public async Task<bool> DispatchAsync(Guid machineActorId,
        CancellationToken cancellationToken = default)
    {
        if (machineActorId == Guid.Empty)
            throw new ArgumentException("Machine actor is required.", nameof(machineActorId));
        var tenantId = tenant.TenantId;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction))
        {
            scope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new NpgsqlCommand("""
            UPDATE tenants SET deletion_started_at=now()
            WHERE id=@tenant AND status='pending_deletion'
              AND deletion_due_at <= now() AND deletion_started_at IS NULL
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, machineActorId,
            "service", "access.tenant.deletion-dispatched", "tenant", tenantId.ToString("D"),
            "Grace period elapsed", cancellationToken);
        var eventId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0", id = eventId, type = "tenant.deleted.v1",
            source = "excellore/access", time = DateTimeOffset.UtcNow,
            tenantid = tenantId, correlationid = Guid.NewGuid(),
            actor = new { sub = machineActorId.ToString("D"), type = "machine" },
            data = new { tenantId }
        });
        await using (var command = new NpgsqlCommand("""
            INSERT INTO outbox (id,type,tenant_id,payload)
            VALUES (@id,'tenant.deleted.v1',@tenant,@payload::jsonb)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", eventId);
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
