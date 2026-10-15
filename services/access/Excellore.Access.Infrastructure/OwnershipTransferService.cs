using System.Text.Json;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record NominateOwnerRequest(Guid UserId, string? Reason);
public enum OwnershipTransferStatus { Created, Accepted, Invalid, Forbidden, StepUpRequired, NotFound, Conflict }
public sealed record OwnershipTransferResult(OwnershipTransferStatus Status, Guid? TransferId = null);

public sealed class OwnershipTransferService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext, ICurrentUser user, AccessResolutionService resolver)
{
    public Task<OwnershipTransferResult> NominateAsync(NominateOwnerRequest? request,
        CancellationToken cancellationToken = default) =>
        ChangeAsync("nominate", Guid.Empty, request, cancellationToken);

    public Task<OwnershipTransferResult> AcceptAsync(Guid transferId,
        CancellationToken cancellationToken = default) =>
        ChangeAsync("accept", transferId, null, cancellationToken);

    private async Task<OwnershipTransferResult> ChangeAsync(string verb, Guid transferId,
        NominateOwnerRequest? request, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId;
        var actorId = user.UserId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", ("key", AccessAuditWriter.AuditLock));
        await ExecuteAsync("SELECT set_config('app.tenant_id', @tenant, true)",
            ("tenant", tenantId.ToString("D")));
        if (await ScalarAsync("SELECT status FROM tenants WHERE id = @tenant FOR UPDATE",
            ("tenant", tenantId)) as string != "active")
            return await DenyAsync(OwnershipTransferStatus.NotFound, "tenant-inactive");
        var current = await resolver.ResolveAsync(actorId, tenantId, cancellationToken);
        if (!current.Allowed)
            return await DenyAsync(OwnershipTransferStatus.NotFound, "membership-inactive");
        var ownerRole = await ScalarAsync("""
            SELECT id FROM roles WHERE tenant_id IS NULL AND is_system AND name = 'owner'
            """) is Guid roleId ? roleId : Guid.Empty;
        if (ownerRole == Guid.Empty)
            return await DenyAsync(OwnershipTransferStatus.Conflict, "owner-role-unavailable");

        if (verb == "nominate")
        {
            if (!current.Permissions.Contains("access.tenant.transfer", StringComparer.Ordinal) ||
                !current.Roles.Contains("owner", StringComparer.Ordinal))
                return await DenyAsync(OwnershipTransferStatus.Forbidden, "owner-required");
            if (!user.HasRecentMfa)
                return await DenyAsync(OwnershipTransferStatus.StepUpRequired, "step-up-required");
            var targetId = request?.UserId ?? Guid.Empty;
            var reason = request?.Reason?.Trim();
            if (targetId == Guid.Empty || !AccessRoleGrantPolicy.ValidReasonCode(reason))
                return await DenyAsync(OwnershipTransferStatus.Invalid, "invalid-nomination");
            if (targetId == actorId)
                return await DenyAsync(OwnershipTransferStatus.Forbidden, "self-nomination");
            if (!await IsActiveOrdinaryMemberAsync(targetId))
                return await DenyAsync(OwnershipTransferStatus.NotFound, "member-unavailable");
            if (await HasOwnerAssignmentAsync(targetId, ownerRole))
                return await DenyAsync(OwnershipTransferStatus.Conflict, "already-owner");
            await ExecuteAsync("""
                UPDATE ownership_transfers SET status = 'expired'
                WHERE tenant_id = @tenant AND from_user_id = @actor
                  AND status = 'pending' AND expires_at <= now()
                """, ("tenant", tenantId), ("actor", actorId));
            if (await ScalarAsync("""
                SELECT EXISTS(SELECT 1 FROM ownership_transfers
                  WHERE tenant_id = @tenant AND from_user_id = @actor AND status = 'pending')
                """, ("tenant", tenantId), ("actor", actorId)) is true)
                return await DenyAsync(OwnershipTransferStatus.Conflict, "nomination-pending");
            var id = Guid.NewGuid();
            await ExecuteAsync("""
                INSERT INTO ownership_transfers
                  (id, tenant_id, from_user_id, to_user_id, reason, status, expires_at)
                VALUES (@id, @tenant, @actor, @target, @reason, 'pending', now() + interval '7 days')
                """, ("id", id), ("tenant", tenantId), ("actor", actorId),
                ("target", targetId), ("reason", reason!));
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", "access.ownership.nominated", "ownership-transfer", id.ToString("D"),
                reason!, cancellationToken,
                afterJson: JsonSerializer.Serialize(new { fromUserId = actorId, toUserId = targetId }),
                acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(OwnershipTransferStatus.Created, id);
        }

        if (transferId == Guid.Empty)
            return await DenyAsync(OwnershipTransferStatus.NotFound, "transfer-unavailable");
        Guid fromId = Guid.Empty;
        Guid nominatedId = Guid.Empty;
        string? reasonCode = null;
        string? transferStatus = null;
        DateTimeOffset expiresAt = default;
        await using (var command = Command("""
            SELECT from_user_id, to_user_id, reason, status, expires_at
            FROM ownership_transfers WHERE tenant_id = @tenant AND id = @id FOR UPDATE
            """, ("tenant", tenantId), ("id", transferId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                fromId = reader.GetGuid(0);
                nominatedId = reader.GetGuid(1);
                reasonCode = reader.GetString(2);
                transferStatus = reader.GetString(3);
                expiresAt = reader.GetFieldValue<DateTimeOffset>(4);
            }
        }
        if (fromId == Guid.Empty || nominatedId != actorId)
            return await DenyAsync(OwnershipTransferStatus.NotFound, "transfer-unavailable");
        if (transferStatus != "pending" || expiresAt <= DateTimeOffset.UtcNow)
            return await DenyAsync(OwnershipTransferStatus.Conflict, "transfer-expired");
        if (!await IsActiveOrdinaryMemberAsync(fromId) ||
            !await IsActiveOrdinaryMemberAsync(actorId) ||
            !await HasOwnerAssignmentAsync(fromId, ownerRole))
            return await DenyAsync(OwnershipTransferStatus.Conflict, "owner-unavailable");
        if (await HasOwnerAssignmentAsync(actorId, ownerRole))
            return await DenyAsync(OwnershipTransferStatus.Conflict, "already-owner");

        var assignmentId = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO role_assignments (id, tenant_id, user_id, role_id, reason, granted_by)
            VALUES (@id, @tenant, @target, @role, @reason, @from)
            """, ("id", assignmentId), ("tenant", tenantId), ("target", actorId),
            ("role", ownerRole), ("reason", reasonCode!), ("from", fromId));
        await ExecuteAsync("""
            UPDATE role_assignments SET revoked_at = now()
            WHERE tenant_id = @tenant AND user_id = @from AND role_id = @role
              AND revoked_at IS NULL
            """, ("tenant", tenantId), ("from", fromId), ("role", ownerRole));
        await ExecuteAsync("""
            UPDATE ownership_transfers SET status = 'accepted', accepted_at = now()
            WHERE tenant_id = @tenant AND id = @id
            """, ("tenant", tenantId), ("id", transferId));
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
            "human", "access.ownership.accepted", "ownership-transfer", transferId.ToString("D"),
            reasonCode!, cancellationToken,
            beforeJson: JsonSerializer.Serialize(new { ownerId = fromId }),
            afterJson: JsonSerializer.Serialize(new { ownerId = actorId }), acr: user.Acr);
        await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction,
            tenantId, actorId, cancellationToken);
        await AccessAuthzChangeWriter.WriteAssignmentEventAsync(connection, transaction,
            tenantId, fromId, ownerRole, null, actorId, "revoked", cancellationToken);
        await AccessAuthzChangeWriter.WriteAssignmentEventAsync(connection, transaction,
            tenantId, actorId, ownerRole, null, actorId, "granted", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OwnershipTransferStatus.Accepted, transferId);

        async Task<OwnershipTransferResult> DenyAsync(OwnershipTransferStatus status, string code)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", $"access.ownership.{verb}.denied", "ownership-transfer",
                transferId == Guid.Empty ? "new" : transferId.ToString("D"), code,
                cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(status);
        }

        async Task<bool> IsActiveOrdinaryMemberAsync(Guid id) =>
            await ScalarAsync("""
                SELECT EXISTS(SELECT 1 FROM tenant_members m
                JOIN identities i ON i.user_id = m.user_id
                WHERE m.tenant_id = @tenant AND m.user_id = @user
                  AND m.status = 'active' AND i.status = 'active' AND NOT m.is_support)
                """, ("tenant", tenantId), ("user", id)) is true;

        async Task<bool> HasOwnerAssignmentAsync(Guid id, Guid role) =>
            await ScalarAsync("""
                SELECT EXISTS(SELECT 1 FROM role_assignments
                  WHERE tenant_id = @tenant AND user_id = @user AND role_id = @role
                    AND revoked_at IS NULL AND (expires_at IS NULL OR expires_at > now()))
                """, ("tenant", tenantId), ("user", id), ("role", role)) is true;

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
}
