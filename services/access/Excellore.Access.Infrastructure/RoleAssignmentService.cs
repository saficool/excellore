using System.Text.Json;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record GrantRoleRequest(Guid UserId, Guid RoleId, DateTimeOffset? ExpiresAt,
    string? Reason);

public sealed record RevokeRoleRequest(string? Reason);

public enum RoleAssignmentStatus
{
    Granted, Revoked, Invalid, Forbidden, StepUpRequired, NotFound, Conflict
}

public sealed record RoleAssignmentResult(RoleAssignmentStatus Status, Guid? AssignmentId = null);

public sealed class RoleAssignmentService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext, ICurrentUser user, AccessResolutionService resolver)
{
    public Task<RoleAssignmentResult> GrantAsync(GrantRoleRequest? request,
        CancellationToken cancellationToken = default) =>
        MutateAsync("grant", Guid.Empty, request, null, cancellationToken);

    public Task<RoleAssignmentResult> RevokeAsync(Guid assignmentId, RevokeRoleRequest? request,
        CancellationToken cancellationToken = default) =>
        MutateAsync("revoke", assignmentId, null, request, cancellationToken);

    private async Task<RoleAssignmentResult> MutateAsync(string action, Guid assignmentId,
        GrantRoleRequest? grant, RevokeRoleRequest? revoke, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId;
        var actorId = user.UserId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", ("key", AccessAuditWriter.AuditLock));
        await ExecuteAsync("SELECT set_config('app.tenant_id', @tenant, true)",
            ("tenant", tenantId.ToString("D")));
        var tenantStatus = await ScalarAsync("SELECT status FROM tenants WHERE id = @tenant FOR UPDATE",
            ("tenant", tenantId)) as string;
        if (tenantStatus != "active")
            return await DenyAsync(RoleAssignmentStatus.NotFound, "tenant-inactive");

        var current = await resolver.ResolveAsync(actorId, tenantId, cancellationToken);
        if (!current.Allowed)
            return await DenyAsync(RoleAssignmentStatus.NotFound, "membership-inactive");
        var actorPermissions = current.Permissions.ToHashSet(StringComparer.Ordinal);
        if (!actorPermissions.Contains("access.assignment.manage"))
            return await DenyAsync(RoleAssignmentStatus.Forbidden, "permission-missing");

        var reason = action == "grant" ? grant?.Reason?.Trim() : revoke?.Reason?.Trim();
        if (!AccessRoleGrantPolicy.ValidReasonCode(reason))
            return await DenyAsync(RoleAssignmentStatus.Invalid, "invalid-reason");
        var validatedReason = reason!;

        Guid targetUserId;
        Guid roleId;
        DateTimeOffset? expiresAt;
        AssignmentState? existing = null;
        if (action == "grant")
        {
            if (grant is null || grant.UserId == Guid.Empty || grant.RoleId == Guid.Empty ||
                grant.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                return await DenyAsync(RoleAssignmentStatus.Invalid, "invalid-grant");
            targetUserId = grant.UserId;
            roleId = grant.RoleId;
            expiresAt = grant.ExpiresAt?.ToUniversalTime();
        }
        else
        {
            if (assignmentId == Guid.Empty)
                return await DenyAsync(RoleAssignmentStatus.NotFound, "assignment-unavailable");
            existing = await ReadAssignmentAsync(assignmentId);
            if (existing is null)
                return await DenyAsync(RoleAssignmentStatus.NotFound, "assignment-unavailable");
            targetUserId = existing.UserId;
            roleId = existing.RoleId;
            expiresAt = existing.ExpiresAt;
        }

        if (targetUserId == actorId)
            return await DenyAsync(RoleAssignmentStatus.Forbidden, "self-assignment");
        if (await ScalarAsync("""
            SELECT EXISTS (SELECT 1 FROM tenant_members m
              JOIN identities i ON i.user_id = m.user_id
              WHERE m.tenant_id = @tenant AND m.user_id = @user
                AND m.status = 'active' AND i.status = 'active')
            """, ("tenant", tenantId), ("user", targetUserId)) is not true)
            return await DenyAsync(RoleAssignmentStatus.NotFound, "member-unavailable");

        var role = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
            tenantId, roleId, cancellationToken);
        if (role is null)
            return await DenyAsync(RoleAssignmentStatus.NotFound, "role-unavailable");
        if (AccessRoleGrantPolicy.Check(role, actorPermissions, user.HasRecentMfa) is { } decision)
            return await DenyAsync(decision, decision switch
            {
                RoleAssignmentStatus.Conflict when role.Name == "owner" => "ownership-transfer-required",
                RoleAssignmentStatus.Conflict => "role-unavailable",
                RoleAssignmentStatus.Forbidden => "role-permission-escalation",
                _ => "step-up-required"
            });

        if (action == "grant")
        {
            var prior = await ReadActiveAssignmentAsync(targetUserId, roleId);
            if (prior is not null)
            {
                if (prior.ExpiresAt is null || prior.ExpiresAt > DateTimeOffset.UtcNow)
                    return await DenyAsync(RoleAssignmentStatus.Conflict, "assignment-exists");
                await ExecuteAsync("""
                    UPDATE role_assignments SET revoked_at = now()
                    WHERE id = @id AND tenant_id = @tenant AND revoked_at IS NULL
                    """, ("id", prior.Id), ("tenant", tenantId));
                await AuditChangeAsync("revoked", prior.Id, prior, null, "expired-grant-replaced");
                await AccessAuthzChangeWriter.WriteAssignmentEventAsync(connection, transaction,
                    tenantId, prior.UserId, prior.RoleId, prior.ExpiresAt, actorId, "revoked",
                    cancellationToken);
            }
            var assigned = new AssignmentState(Guid.NewGuid(), targetUserId, roleId,
                expiresAt, validatedReason);
            await ExecuteAsync("""
                INSERT INTO role_assignments
                  (id, tenant_id, user_id, role_id, expires_at, reason, granted_by)
                VALUES (@id, @tenant, @user, @role, @expires, @reason, @actor)
                """, ("id", assigned.Id), ("tenant", tenantId), ("user", targetUserId),
                ("role", roleId), ("expires", (object?)expiresAt ?? DBNull.Value),
                ("reason", validatedReason), ("actor", actorId));
            await AuditChangeAsync("granted", assigned.Id, null, assigned, validatedReason);
            await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction,
                tenantId, actorId, cancellationToken);
            await AccessAuthzChangeWriter.WriteAssignmentEventAsync(connection, transaction,
                tenantId, targetUserId, roleId, expiresAt, actorId, "granted", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(RoleAssignmentStatus.Granted, assigned.Id);
        }

        await ExecuteAsync("""
            UPDATE role_assignments SET revoked_at = now()
            WHERE id = @id AND tenant_id = @tenant AND revoked_at IS NULL
            """, ("id", assignmentId), ("tenant", tenantId));
        await AuditChangeAsync("revoked", assignmentId, existing, null, validatedReason);
        await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction,
            tenantId, actorId, cancellationToken);
        await AccessAuthzChangeWriter.WriteAssignmentEventAsync(connection, transaction,
            tenantId, targetUserId, roleId, expiresAt, actorId, "revoked", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(RoleAssignmentStatus.Revoked, assignmentId);

        async Task<RoleAssignmentResult> DenyAsync(RoleAssignmentStatus denied, string code)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", $"access.assignment.{action}.denied", "assignment",
                assignmentId == Guid.Empty ? "new" : assignmentId.ToString("D"), code,
                cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(denied);
        }

        async Task AuditChangeAsync(string verb, Guid id, AssignmentState? before,
            AssignmentState? after, string auditReason) =>
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", $"access.assignment.{verb}", "assignment", id.ToString("D"),
                auditReason, cancellationToken,
                beforeJson: before is null ? null : JsonSerializer.Serialize(before),
                afterJson: after is null ? null : JsonSerializer.Serialize(after), acr: user.Acr);

        async Task<AssignmentState?> ReadAssignmentAsync(Guid id)
        {
            await using var command = Command("""
                SELECT id, user_id, role_id, expires_at, reason
                FROM role_assignments WHERE id = @id AND tenant_id = @tenant
                  AND revoked_at IS NULL FOR UPDATE
                """, ("id", id), ("tenant", tenantId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? new AssignmentState(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)) : null;
        }

        async Task<AssignmentState?> ReadActiveAssignmentAsync(Guid target, Guid role)
        {
            await using var command = Command("""
                SELECT id, user_id, role_id, expires_at, reason
                FROM role_assignments
                WHERE tenant_id = @tenant AND user_id = @user AND role_id = @role
                  AND revoked_at IS NULL FOR UPDATE
                """, ("tenant", tenantId), ("user", target), ("role", role));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? new AssignmentState(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)) : null;
        }

        async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = Command(sql, parameters);
            return await command.ExecuteScalarAsync(cancellationToken);
        }

        async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = Command(sql, parameters);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        NpgsqlCommand Command(string sql, params (string Name, object Value)[] parameters)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return command;
        }
    }

    private sealed record AssignmentState(Guid Id, Guid UserId, Guid RoleId,
        DateTimeOffset? ExpiresAt, string? Reason);

}
