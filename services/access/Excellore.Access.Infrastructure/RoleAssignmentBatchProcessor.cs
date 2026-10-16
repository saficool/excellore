using System.Text.Json;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Access.Infrastructure;

/// <summary>Processes one target per tenant transaction so retries cannot duplicate a grant.</summary>
public sealed class RoleAssignmentBatchProcessor(NpgsqlDataSource database, ITenantIdAccessor tenant)
{
    public async Task<bool> ProcessOneAsync(Guid machineActorId,
        CancellationToken cancellationToken = default)
    {
        if (machineActorId == Guid.Empty)
            throw new ArgumentException("Machine actor is required.", nameof(machineActorId));
        var tenantId = tenant.TenantId;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(@lock);
            SELECT set_config('app.tenant_id', @tenant, true)
            """, connection, transaction))
        {
            scope.Parameters.AddWithValue("lock", AccessAuditWriter.AuditLock);
            scope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }

        Batch? batch;
        await using (var command = new NpgsqlCommand("""
            SELECT id,requested_by,role_id,target_user_ids,expires_at,reason,cursor
            FROM role_assignment_batches
            WHERE tenant_id=@tenant AND status IN ('pending','running')
            ORDER BY created_at,id LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            batch = await reader.ReadAsync(cancellationToken)
                ? new Batch(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                    reader.GetFieldValue<Guid[]>(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetString(5), reader.GetInt32(6)) : null;
        }
        if (batch is null) return false;
        if (batch.Cursor >= batch.Targets.Length)
            throw new InvalidOperationException("Role assignment batch cursor is invalid.");

        var targetId = batch.Targets[batch.Cursor];
        var outcome = "target-unavailable";
        Guid? assignmentId = null;
        var ownerActive = await RoleAssignmentBatchPolicy.ActiveOwnerAsync(connection,
            transaction, tenantId, batch.RequestedBy, cancellationToken);
        if (!ownerActive) outcome = "requester-no-longer-owner";
        else if (batch.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            outcome = "grant-expired";
        else
        {
            var role = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
                tenantId, batch.RoleId, cancellationToken);
            if (role is null || !RoleAssignmentBatchPolicy.SafeRole(role))
                outcome = "role-unavailable";
            else if (targetId == batch.RequestedBy || !await ActiveTargetAsync(connection,
                         transaction, tenantId, targetId, cancellationToken))
                outcome = "target-unavailable";
            else if (await AssignmentExistsAsync(connection, transaction, tenantId,
                         targetId, batch.RoleId, cancellationToken))
                outcome = "assignment-exists";
            else
            {
                assignmentId = Guid.NewGuid();
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO role_assignments
                      (id,tenant_id,user_id,role_id,expires_at,reason,granted_by)
                    VALUES (@id,@tenant,@user,@role,@expires,@reason,@actor)
                    """, connection, transaction);
                insert.Parameters.AddWithValue("id", assignmentId.Value);
                insert.Parameters.AddWithValue("tenant", tenantId);
                insert.Parameters.AddWithValue("user", targetId);
                insert.Parameters.AddWithValue("role", batch.RoleId);
                insert.Parameters.AddWithValue("expires", (object?)batch.ExpiresAt ?? DBNull.Value);
                insert.Parameters.AddWithValue("reason", batch.Reason);
                insert.Parameters.AddWithValue("actor", machineActorId);
                await insert.ExecuteNonQueryAsync(cancellationToken);
                await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction, tenantId,
                    machineActorId, cancellationToken, "machine");
                await AccessAuthzChangeWriter.WriteAssignmentEventAsync(connection, transaction,
                    tenantId, targetId, batch.RoleId, batch.ExpiresAt, machineActorId,
                    "granted", cancellationToken, "machine");
                outcome = "granted";
            }
        }

        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, machineActorId,
            "service", outcome == "granted" ? "access.assignment.batch.granted" :
                "access.assignment.batch.skipped", "assignment-batch", batch.Id.ToString("D"),
            outcome == "granted" ? batch.Reason : outcome, cancellationToken,
            afterJson: JsonSerializer.Serialize(new
            {
                batchId = batch.Id, targetUserId = targetId, assignmentId, requestedBy = batch.RequestedBy,
                result = outcome
            }));
        var next = batch.Cursor + 1;
        await using (var update = new NpgsqlCommand("""
            UPDATE role_assignment_batches SET cursor=@cursor,
              succeeded=succeeded+@success,failed=failed+@failure,
              status=CASE WHEN @done THEN 'completed' ELSE 'running' END,
              completed_at=CASE WHEN @done THEN now() ELSE completed_at END
            WHERE id=@id AND tenant_id=@tenant
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("cursor", next);
            update.Parameters.AddWithValue("success", assignmentId is null ? 0 : 1);
            update.Parameters.AddWithValue("failure", assignmentId is null ? 1 : 0);
            update.Parameters.AddWithValue("done", next == batch.Targets.Length);
            update.Parameters.AddWithValue("id", batch.Id);
            update.Parameters.AddWithValue("tenant", tenantId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> ActiveTargetAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, Guid userId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM tenant_members m JOIN identities i ON i.user_id=m.user_id
              WHERE m.tenant_id=@tenant AND m.user_id=@user AND m.status='active'
                AND NOT m.is_support AND i.status='active')
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", userId);
        return (bool)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<bool> AssignmentExistsAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, Guid userId, Guid roleId,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM role_assignments WHERE tenant_id=@tenant
              AND user_id=@user AND role_id=@role AND revoked_at IS NULL)
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", roleId);
        return (bool)(await command.ExecuteScalarAsync(token))!;
    }

    private sealed record Batch(Guid Id, Guid RequestedBy, Guid RoleId, Guid[] Targets,
        DateTimeOffset? ExpiresAt, string Reason, int Cursor);
}
