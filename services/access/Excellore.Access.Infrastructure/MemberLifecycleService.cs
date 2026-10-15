using System.Text.Json;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record MemberChangeRequest(Guid UserId, string? Reason);
public enum MemberLifecycleStatus { Changed, Invalid, Forbidden, StepUpRequired, NotFound, Conflict }
public sealed record MemberLifecycleResult(MemberLifecycleStatus Status);

public sealed class MemberLifecycleService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext, ICurrentUser user, AccessResolutionService resolver)
{
    public Task<MemberLifecycleResult> SuspendAsync(MemberChangeRequest? request,
        CancellationToken cancellationToken = default) => MutateAsync("suspended", request, cancellationToken);

    public Task<MemberLifecycleResult> ReactivateAsync(MemberChangeRequest? request,
        CancellationToken cancellationToken = default) => MutateAsync("reactivated", request, cancellationToken);

    public Task<MemberLifecycleResult> RemoveAsync(MemberChangeRequest? request,
        CancellationToken cancellationToken = default) => MutateAsync("removed", request, cancellationToken);

    private async Task<MemberLifecycleResult> MutateAsync(string verb, MemberChangeRequest? request,
        CancellationToken cancellationToken)
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
            return await DenyAsync(MemberLifecycleStatus.NotFound, "tenant-inactive");

        var current = await resolver.ResolveAsync(actorId, tenantId, cancellationToken);
        if (!current.Allowed)
            return await DenyAsync(MemberLifecycleStatus.NotFound, "membership-inactive");
        var actorPermissions = current.Permissions.ToHashSet(StringComparer.Ordinal);
        var requiredPermission = verb == "removed" ? "access.member.remove" : "access.member.suspend";
        if (!actorPermissions.Contains(requiredPermission))
            return await DenyAsync(MemberLifecycleStatus.Forbidden, "permission-missing");

        var targetId = request?.UserId ?? Guid.Empty;
        var reason = request?.Reason?.Trim();
        if (targetId == Guid.Empty || !AccessRoleGrantPolicy.ValidReasonCode(reason))
            return await DenyAsync(MemberLifecycleStatus.Invalid, "invalid-member-change");
        if (targetId == actorId)
            return await DenyAsync(MemberLifecycleStatus.Forbidden, "self-change");

        string? oldStatus = null;
        string? identityStatus = null;
        var isSupport = false;
        await using (var command = Command("""
            SELECT m.status, i.status, m.is_support FROM tenant_members m
            JOIN identities i ON i.user_id = m.user_id
            WHERE m.tenant_id = @tenant AND m.user_id = @target FOR UPDATE OF m
            """, ("tenant", tenantId), ("target", targetId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                oldStatus = reader.GetString(0);
                identityStatus = reader.GetString(1);
                isSupport = reader.GetBoolean(2);
            }
        }
        if (oldStatus is null)
            return await DenyAsync(MemberLifecycleStatus.NotFound, "member-unavailable");
        if (isSupport)
            return await DenyAsync(MemberLifecycleStatus.Forbidden, "support-grant-required");
        var validTransition = verb switch
        {
            "suspended" => oldStatus == "active",
            "reactivated" => oldStatus == "suspended" && identityStatus == "active",
            "removed" => oldStatus is "active" or "suspended",
            _ => false
        };
        if (!validTransition)
            return await DenyAsync(MemberLifecycleStatus.Conflict, "member-state-conflict");

        var ownerRoleId = await ScalarAsync("""
            SELECT id FROM roles WHERE tenant_id IS NULL AND is_system AND name = 'owner'
            """) is Guid id ? id : Guid.Empty;
        if (ownerRoleId == Guid.Empty)
            return await DenyAsync(MemberLifecycleStatus.Conflict, "owner-role-unavailable");
        var roleIds = new HashSet<Guid>();
        await using (var command = Command("""
            SELECT ra.role_id FROM role_assignments ra
            WHERE ra.tenant_id = @tenant AND ra.user_id = @target
              AND ra.revoked_at IS NULL AND (ra.expires_at IS NULL OR ra.expires_at > now())
            UNION
            SELECT gra.role_id FROM group_members gm
            JOIN group_role_assignments gra ON gra.tenant_id = gm.tenant_id
              AND gra.group_id = gm.group_id
            WHERE gm.tenant_id = @tenant AND gm.user_id = @target
              AND gra.revoked_at IS NULL AND (gra.expires_at IS NULL OR gra.expires_at > now())
            """, ("tenant", tenantId), ("target", targetId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) roleIds.Add(reader.GetGuid(0));

        var targetIsOwner = roleIds.Contains(ownerRoleId);
        if (targetIsOwner)
        {
            if (!actorPermissions.Contains("access.tenant.transfer"))
                return await DenyAsync(MemberLifecycleStatus.Forbidden, "owner-change-denied");
            if (!user.HasRecentMfa)
                return await DenyAsync(MemberLifecycleStatus.StepUpRequired, "owner-step-up-required");
            if (verb != "reactivated" && oldStatus == "active" &&
                await ScalarAsync("""
                    SELECT EXISTS (
                      SELECT 1 FROM tenant_members m
                      JOIN identities i ON i.user_id = m.user_id
                      WHERE m.tenant_id = @tenant AND m.user_id <> @target
                        AND m.status = 'active' AND i.status = 'active'
                        AND (EXISTS (
                          SELECT 1 FROM role_assignments ra
                          WHERE ra.tenant_id = m.tenant_id AND ra.user_id = m.user_id
                            AND ra.role_id = @owner AND ra.revoked_at IS NULL
                            AND (ra.expires_at IS NULL OR ra.expires_at > now()))
                          OR EXISTS (
                          SELECT 1 FROM group_members gm
                          JOIN group_role_assignments gra ON gra.tenant_id = gm.tenant_id
                            AND gra.group_id = gm.group_id
                          WHERE gm.tenant_id = m.tenant_id AND gm.user_id = m.user_id
                            AND gra.role_id = @owner AND gra.revoked_at IS NULL
                            AND (gra.expires_at IS NULL OR gra.expires_at > now())))
                    )
                    """, ("tenant", tenantId), ("target", targetId),
                    ("owner", ownerRoleId)) is not true)
                return await DenyAsync(MemberLifecycleStatus.Conflict, "last-owner");
        }
        if (verb == "reactivated")
            foreach (var roleId in roleIds.Where(roleId => roleId != ownerRoleId))
            {
                var role = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
                    tenantId, roleId, cancellationToken);
                if (role is null)
                    return await DenyAsync(MemberLifecycleStatus.Conflict, "role-unavailable");
                if (AccessRoleGrantPolicy.Check(role, actorPermissions, user.HasRecentMfa)
                    is { } decision)
                    return await DenyAsync(decision switch
                    {
                        RoleAssignmentStatus.Forbidden => MemberLifecycleStatus.Forbidden,
                        RoleAssignmentStatus.StepUpRequired => MemberLifecycleStatus.StepUpRequired,
                        _ => MemberLifecycleStatus.Conflict
                    }, "role-policy-denied");
            }

        var newStatus = verb == "reactivated" ? "active" : verb;
        await ExecuteAsync("""
            UPDATE tenant_members SET status = @status, updated_at = now()
            WHERE tenant_id = @tenant AND user_id = @target
            """, ("status", newStatus), ("tenant", tenantId), ("target", targetId));
        if (verb == "removed")
        {
            await ExecuteAsync("""
                UPDATE role_assignments SET revoked_at = now()
                WHERE tenant_id = @tenant AND user_id = @target AND revoked_at IS NULL
                """, ("tenant", tenantId), ("target", targetId));
            await ExecuteAsync("""
                DELETE FROM group_members WHERE tenant_id = @tenant AND user_id = @target
                """, ("tenant", tenantId), ("target", targetId));
        }
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
            "human", $"access.member.{verb}", "member", targetId.ToString("D"), reason!,
            cancellationToken, beforeJson: JsonSerializer.Serialize(new { status = oldStatus }),
            afterJson: JsonSerializer.Serialize(new { status = newStatus }), acr: user.Acr);
        await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction,
            tenantId, actorId, cancellationToken);
        await WriteMemberEventAsync();
        await transaction.CommitAsync(cancellationToken);
        return new(MemberLifecycleStatus.Changed);

        async Task<MemberLifecycleResult> DenyAsync(MemberLifecycleStatus status, string safeReason)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", $"access.member.{verb}.denied", "member",
                targetIdForAudit(), safeReason, cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(status);
        }

        string targetIdForAudit() => request?.UserId is Guid id && id != Guid.Empty
            ? id.ToString("D") : "unknown";

        async Task WriteMemberEventAsync()
        {
            var eventId = Guid.NewGuid();
            var eventType = $"tenant.member.{verb}.v1";
            var payload = JsonSerializer.Serialize(new
            {
                specversion = "1.0", id = eventId, type = eventType,
                source = "excellore/access", time = DateTimeOffset.UtcNow,
                tenantid = tenantId, correlationid = Guid.NewGuid(),
                actor = new { sub = actorId.ToString("D"), type = "human" },
                data = new { tenantId, userId = targetId }
            });
            await ExecuteAsync("""
                INSERT INTO outbox (id, type, tenant_id, payload)
                VALUES (@id, @type, @tenant, @payload::jsonb)
                """, ("id", eventId), ("type", eventType), ("tenant", tenantId),
                ("payload", payload));
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
}
