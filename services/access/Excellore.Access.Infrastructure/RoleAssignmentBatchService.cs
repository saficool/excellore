using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record CreateRoleAssignmentBatchRequest(Guid RoleId, Guid[] TargetUserIds,
    DateTimeOffset? ExpiresAt, string? Reason);

public sealed record RoleAssignmentBatchView(Guid Id, string Status, int Total, int Processed,
    int Succeeded, int Failed, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public enum RoleAssignmentBatchStatus
{
    Accepted, Invalid, Forbidden, StepUpRequired, NotFound, Conflict
}

public sealed record RoleAssignmentBatchResult(RoleAssignmentBatchStatus Status,
    RoleAssignmentBatchView? Batch = null);

/// <summary>Queues bounded owner-approved grants; processing always rechecks live authority.</summary>
public sealed class RoleAssignmentBatchService(NpgsqlDataSource database,
    ITenantContextAccessor tenantContext, ICurrentUser user)
{
    public async Task<RoleAssignmentBatchResult> CreateAsync(
        CreateRoleAssignmentBatchRequest? request, CancellationToken cancellationToken = default)
    {
        var targets = request?.TargetUserIds;
        if (request is null || request.RoleId == Guid.Empty || targets is null ||
            targets.Length is < 1 or > 100 || targets.Any(id => id == Guid.Empty || id == user.UserId) ||
            targets.Distinct().Count() != targets.Length ||
            !AccessRoleGrantPolicy.ValidReasonCode(request.Reason?.Trim()) ||
            request.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return new(RoleAssignmentBatchStatus.Invalid);
        if (!user.HasRecentMfa) return new(RoleAssignmentBatchStatus.StepUpRequired);

        var tenantId = tenantContext.TenantId;
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
        if (!await RoleAssignmentBatchPolicy.ActiveOwnerAsync(connection, transaction,
                tenantId, user.UserId, cancellationToken))
            return new(RoleAssignmentBatchStatus.Forbidden);

        var role = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
            tenantId, request.RoleId, cancellationToken);
        if (role is null) return new(RoleAssignmentBatchStatus.NotFound);
        if (!RoleAssignmentBatchPolicy.SafeRole(role))
            return new(RoleAssignmentBatchStatus.Forbidden);
        await using (var members = new NpgsqlCommand("""
            SELECT count(*) FROM tenant_members m JOIN identities i ON i.user_id=m.user_id
            WHERE m.tenant_id=@tenant AND m.user_id=ANY(@targets)
              AND m.status='active' AND NOT m.is_support AND i.status='active'
            """, connection, transaction))
        {
            members.Parameters.AddWithValue("tenant", tenantId);
            members.Parameters.AddWithValue("targets", targets);
            if ((long)(await members.ExecuteScalarAsync(cancellationToken))! != targets.Length)
                return new(RoleAssignmentBatchStatus.NotFound);
        }

        var id = Guid.NewGuid();
        try
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO role_assignment_batches
                  (id, tenant_id, requested_by, role_id, target_user_ids, expires_at, reason)
                VALUES (@id, @tenant, @actor, @role, @targets, @expires, @reason)
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("actor", user.UserId);
            insert.Parameters.AddWithValue("role", request.RoleId);
            insert.Parameters.AddWithValue("targets", targets);
            insert.Parameters.AddWithValue("expires", (object?)request.ExpiresAt?.ToUniversalTime()
                ?? DBNull.Value);
            insert.Parameters.AddWithValue("reason", request.Reason!.Trim());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation &&
                                             error.ConstraintName == "role_assignment_batches_one_pending")
        {
            return new(RoleAssignmentBatchStatus.Conflict);
        }
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, user.UserId,
            "human", "access.assignment.batch.requested", "assignment-batch", id.ToString("D"),
            request.Reason!.Trim(), cancellationToken, acr: user.Acr);
        await transaction.CommitAsync(cancellationToken);
        return new(RoleAssignmentBatchStatus.Accepted,
            new RoleAssignmentBatchView(id, "pending", targets.Length, 0, 0, 0,
                DateTimeOffset.UtcNow, null));
    }

    public async Task<RoleAssignmentBatchView?> GetAsync(Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) return null;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            scope.Parameters.AddWithValue("tenant", tenantContext.TenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand("""
            SELECT id,status,cardinality(target_user_ids),cursor,succeeded,failed,created_at,completed_at
            FROM role_assignment_batches WHERE id=@id AND tenant_id=@tenant
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));
    }
}

internal static class RoleAssignmentBatchPolicy
{
    internal static bool SafeRole(GrantableRole role) =>
        role.Name != "owner" && !role.Privileged &&
        role.Permissions.All(permission => permission.Enabled &&
            !permission.Deprecated && !permission.Privileged);

    internal static async Task<bool> ActiveOwnerAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, Guid actorId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM tenants t
              JOIN tenant_members m ON m.tenant_id=t.id
              JOIN identities i ON i.user_id=m.user_id
              JOIN role_assignments ra ON ra.tenant_id=m.tenant_id AND ra.user_id=m.user_id
              JOIN roles r ON r.id=ra.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE t.id=@tenant AND t.status='active' AND m.user_id=@actor
                AND m.status='active' AND NOT m.is_support AND i.status='active'
                AND ra.revoked_at IS NULL AND (ra.expires_at IS NULL OR ra.expires_at>now())
                AND r.name='owner' AND r.is_system AND r.deleted_at IS NULL
                AND rp.permission_key='access.assignment.manage')
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("actor", actorId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
