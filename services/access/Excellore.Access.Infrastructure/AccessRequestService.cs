using System.Text.Json;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record CreateAccessRequest(string? TargetType, string? TargetKey,
    string? Reason, DateTimeOffset? RequestedUntil);
public sealed record DecideAccessRequest(string? Reason);
public sealed record AccessRequestView(Guid Id, Guid RequesterId, string TargetType,
    string TargetKey, string Reason, DateTimeOffset? RequestedUntil, string Status,
    DateTimeOffset CreatedAt);
public enum AccessRequestStatus { Created, Approved, Rejected, Invalid, Forbidden, StepUpRequired, NotFound, Conflict }
public sealed record AccessRequestResult(AccessRequestStatus Status, Guid? RequestId = null);

public sealed class AccessRequestService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext, ICurrentUser user, AccessResolutionService resolver)
{
    public async Task<IReadOnlyList<AccessRequestView>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<AccessRequestView>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, requester_id, target_type, target_key, reason,
                   requested_until, status, created_at
            FROM access_requests WHERE tenant_id = @tenant
            ORDER BY created_at DESC, id DESC LIMIT 100
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new AccessRequestView(reader.GetGuid(0), reader.GetGuid(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7)));
        return result;
    }

    public Task<AccessRequestResult> CreateAsync(CreateAccessRequest? request,
        CancellationToken cancellationToken = default) =>
        ChangeAsync("create", Guid.Empty, request, null, cancellationToken);

    public Task<AccessRequestResult> ApproveAsync(Guid id, DecideAccessRequest? request,
        CancellationToken cancellationToken = default) =>
        ChangeAsync("approve", id, null, request, cancellationToken);

    public Task<AccessRequestResult> RejectAsync(Guid id, DecideAccessRequest? request,
        CancellationToken cancellationToken = default) =>
        ChangeAsync("reject", id, null, request, cancellationToken);

    private async Task<AccessRequestResult> ChangeAsync(string verb, Guid requestId,
        CreateAccessRequest? create, DecideAccessRequest? decide,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId;
        var actorId = user.UserId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", ("key", AccessAuditWriter.AuditLock));
        await SetTenantAsync(connection, transaction, cancellationToken);
        if (await ScalarAsync("SELECT status FROM tenants WHERE id = @tenant FOR UPDATE",
            ("tenant", tenantId)) as string != "active")
            return await DenyAsync(AccessRequestStatus.NotFound, "tenant-inactive");
        var current = await resolver.ResolveAsync(actorId, tenantId, cancellationToken);
        if (!current.Allowed)
            return await DenyAsync(AccessRequestStatus.NotFound, "membership-inactive");
        var required = verb == "create" ? "access.request.create" : "access.request.approve";
        if (!current.Permissions.Contains(required, StringComparer.Ordinal))
            return await DenyAsync(AccessRequestStatus.Forbidden, "permission-missing");
        var reason = (verb == "create" ? create?.Reason : decide?.Reason)?.Trim();
        if (!AccessRoleGrantPolicy.ValidReasonCode(reason))
            return await DenyAsync(AccessRequestStatus.Invalid, "invalid-reason");

        if (verb == "create")
        {
            var type = create?.TargetType;
            var key = create?.TargetKey;
            if (type is not ("role" or "app") || string.IsNullOrWhiteSpace(key) ||
                create!.RequestedUntil is { } until && until <= DateTimeOffset.UtcNow)
                return await DenyAsync(AccessRequestStatus.Invalid, "invalid-target");
            if (type == "role")
            {
                if (!Guid.TryParse(key, out var roleId))
                    return await DenyAsync(AccessRequestStatus.Invalid, "invalid-target");
                var role = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
                    tenantId, roleId, cancellationToken);
                if (role is null)
                    return await DenyAsync(AccessRequestStatus.NotFound, "role-unavailable");
                if (role.Name == "owner")
                    return await DenyAsync(AccessRequestStatus.Conflict, "ownership-transfer-required");
                key = roleId.ToString("D");
            }
            else
            {
                if (key.Length > 40 || key[0] is < 'a' or > 'z' ||
                    key.Skip(1).Any(character => character is not (>= 'a' and <= 'z')
                        and not (>= '0' and <= '9') and not '-'))
                    return await DenyAsync(AccessRequestStatus.Invalid, "invalid-app");
                if (await ScalarAsync("SELECT EXISTS(SELECT 1 FROM apps WHERE id = @app)",
                    ("app", key)) is not true)
                    return await DenyAsync(AccessRequestStatus.NotFound, "app-unavailable");
            }
            if (await ScalarAsync("""
                SELECT EXISTS(SELECT 1 FROM access_requests
                  WHERE tenant_id = @tenant AND requester_id = @actor
                    AND target_type = @type AND target_key = @key AND status = 'pending')
                """, ("tenant", tenantId), ("actor", actorId), ("type", type!),
                ("key", key!)) is true)
                return await DenyAsync(AccessRequestStatus.Conflict, "request-pending");
            requestId = Guid.NewGuid();
            await ExecuteAsync("""
                INSERT INTO access_requests
                  (id, tenant_id, requester_id, target_type, target_key,
                   reason, requested_until, status)
                VALUES (@id, @tenant, @actor, @type, @key, @reason, @until, 'pending')
                """, ("id", requestId), ("tenant", tenantId), ("actor", actorId),
                ("type", type!), ("key", key!), ("reason", reason!),
                ("until", (object?)create.RequestedUntil?.ToUniversalTime() ?? DBNull.Value));
            await AuditAsync("created", reason!, actorId, type!, key!);
            await transaction.CommitAsync(cancellationToken);
            return new(AccessRequestStatus.Created, requestId);
        }

        if (requestId == Guid.Empty)
            return await DenyAsync(AccessRequestStatus.NotFound, "request-unavailable");
        Guid requesterId = Guid.Empty;
        string? targetType = null;
        string? targetKey = null;
        DateTimeOffset? requestedUntil = null;
        string? statusCode = null;
        await using (var command = Command("""
            SELECT requester_id, target_type, target_key, requested_until, status
            FROM access_requests WHERE tenant_id = @tenant AND id = @id FOR UPDATE
            """, ("tenant", tenantId), ("id", requestId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                requesterId = reader.GetGuid(0);
                targetType = reader.GetString(1);
                targetKey = reader.GetString(2);
                requestedUntil = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
                statusCode = reader.GetString(4);
            }
        }
        if (requesterId == Guid.Empty)
            return await DenyAsync(AccessRequestStatus.NotFound, "request-unavailable");
        if (requesterId == actorId)
            return await DenyAsync(AccessRequestStatus.Forbidden, "self-approval");
        if (statusCode != "pending")
            return await DenyAsync(AccessRequestStatus.Conflict, "already-decided");

        if (verb == "approve")
        {
            if (targetType != "role" || !Guid.TryParse(targetKey, out var roleId))
                return await DenyAsync(AccessRequestStatus.Conflict, "platform-review-required");
            if (requestedUntil is { } until && until <= DateTimeOffset.UtcNow)
                return await DenyAsync(AccessRequestStatus.Conflict, "requested-expiry-passed");
            if (await ScalarAsync("""
                SELECT EXISTS(SELECT 1 FROM tenant_members m
                JOIN identities i ON i.user_id = m.user_id
                WHERE m.tenant_id = @tenant AND m.user_id = @requester
                  AND m.status = 'active' AND i.status = 'active' AND NOT m.is_support)
                """, ("tenant", tenantId), ("requester", requesterId)) is not true)
                return await DenyAsync(AccessRequestStatus.Conflict, "requester-inactive");
            var role = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
                tenantId, roleId, cancellationToken);
            if (role is null)
                return await DenyAsync(AccessRequestStatus.Conflict, "role-unavailable");
            if (AccessRoleGrantPolicy.Check(role,
                current.Permissions.ToHashSet(StringComparer.Ordinal), user.HasRecentMfa) is { } decision)
                return await DenyAsync(decision switch
                {
                    RoleAssignmentStatus.Forbidden => AccessRequestStatus.Forbidden,
                    RoleAssignmentStatus.StepUpRequired => AccessRequestStatus.StepUpRequired,
                    _ => AccessRequestStatus.Conflict
                }, "role-policy-denied");
            if (await ScalarAsync("""
                SELECT EXISTS(SELECT 1 FROM role_assignments
                  WHERE tenant_id = @tenant AND user_id = @requester AND role_id = @role
                    AND revoked_at IS NULL AND (expires_at IS NULL OR expires_at > now()))
                """, ("tenant", tenantId), ("requester", requesterId),
                ("role", roleId)) is true)
                return await DenyAsync(AccessRequestStatus.Conflict, "assignment-exists");
            await ExecuteAsync("""
                UPDATE role_assignments SET revoked_at = now()
                WHERE tenant_id = @tenant AND user_id = @requester AND role_id = @role
                  AND revoked_at IS NULL
                """, ("tenant", tenantId), ("requester", requesterId), ("role", roleId));
            await ExecuteAsync("""
                INSERT INTO role_assignments
                  (id, tenant_id, user_id, role_id, expires_at, reason, granted_by)
                VALUES (@id, @tenant, @requester, @role, @until, @reason, @actor)
                """, ("id", Guid.NewGuid()), ("tenant", tenantId),
                ("requester", requesterId), ("role", roleId),
                ("until", (object?)requestedUntil ?? DBNull.Value),
                ("reason", reason!), ("actor", actorId));
            await ExecuteAsync("""
                UPDATE access_requests SET status = 'approved', decided_by = @actor,
                  decided_at = now(), decision_note = @reason
                WHERE tenant_id = @tenant AND id = @id
                """, ("actor", actorId), ("reason", reason!),
                ("tenant", tenantId), ("id", requestId));
            await AuditAsync("approved", reason!, requesterId, "role", targetKey!);
            await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction,
                tenantId, actorId, cancellationToken);
            await AccessAuthzChangeWriter.WriteAssignmentEventAsync(connection, transaction,
                tenantId, requesterId, roleId, requestedUntil, actorId,
                "granted", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(AccessRequestStatus.Approved, requestId);
        }

        await ExecuteAsync("""
            UPDATE access_requests SET status = 'rejected', decided_by = @actor,
              decided_at = now(), decision_note = @reason
            WHERE tenant_id = @tenant AND id = @id
            """, ("actor", actorId), ("reason", reason!),
            ("tenant", tenantId), ("id", requestId));
        await AuditAsync("rejected", reason!, requesterId, targetType!, targetKey!);
        await transaction.CommitAsync(cancellationToken);
        return new(AccessRequestStatus.Rejected, requestId);

        async Task<AccessRequestResult> DenyAsync(AccessRequestStatus status, string code)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", $"access.request.{verb}.denied", "access-request",
                requestId == Guid.Empty ? "new" : requestId.ToString("D"), code,
                cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(status);
        }

        async Task AuditAsync(string action, string auditReason, Guid target,
            string type, string key) =>
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", $"access.request.{action}", "access-request", requestId.ToString("D"),
                auditReason, cancellationToken,
                afterJson: JsonSerializer.Serialize(new { requesterId = target, targetType = type,
                    targetKey = key, status = action }), acr: user.Acr);

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

    private async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
