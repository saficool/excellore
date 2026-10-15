using System.Text.Json;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record CreateGroupRequest(string? Name, string? Description);
public sealed record ChangeGroupMemberRequest(Guid UserId, string? Reason);
public sealed record GrantGroupRoleRequest(Guid RoleId, DateTimeOffset? ExpiresAt, string? Reason);
public sealed record RevokeGroupRoleRequest(string? Reason);
public sealed record GroupSummary(Guid Id, string Name, string? Description,
    long MemberCount, long RoleAssignmentCount);

public enum GroupMutationStatus
{
    Created, Changed, Unchanged, Invalid, Forbidden, StepUpRequired, NotFound, Conflict
}

public sealed record GroupMutationResult(GroupMutationStatus Status, Guid? Id = null);

public sealed class AccessGroupService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext, ICurrentUser user, AccessResolutionService resolver)
{
    public async Task<IReadOnlyList<GroupSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var setting = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            setting.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await setting.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand("""
            SELECT g.id, g.name, g.description,
              (SELECT count(*) FROM group_members gm
               WHERE gm.tenant_id = g.tenant_id AND gm.group_id = g.id),
              (SELECT count(*) FROM group_role_assignments gra
               WHERE gra.tenant_id = g.tenant_id AND gra.group_id = g.id
                 AND gra.revoked_at IS NULL AND (gra.expires_at IS NULL OR gra.expires_at > now()))
            FROM groups g WHERE g.tenant_id = @tenant ORDER BY lower(g.name), g.id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        var groups = new List<GroupSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                groups.Add(new GroupSummary(reader.GetGuid(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3),
                    reader.GetInt64(4)));
        await transaction.CommitAsync(cancellationToken);
        return groups;
    }

    public Task<GroupMutationResult> CreateAsync(CreateGroupRequest? request,
        CancellationToken cancellationToken = default) =>
        MutateAsync("create", Guid.Empty, Guid.Empty, request, null, null, null, cancellationToken);

    public Task<GroupMutationResult> AddMemberAsync(Guid groupId, ChangeGroupMemberRequest? request,
        CancellationToken cancellationToken = default) =>
        MutateAsync("member-add", groupId, Guid.Empty, null, request, null, null, cancellationToken);

    public Task<GroupMutationResult> RemoveMemberAsync(Guid groupId, ChangeGroupMemberRequest? request,
        CancellationToken cancellationToken = default) =>
        MutateAsync("member-remove", groupId, Guid.Empty, null, request, null, null, cancellationToken);

    public Task<GroupMutationResult> GrantRoleAsync(Guid groupId, GrantGroupRoleRequest? request,
        CancellationToken cancellationToken = default) =>
        MutateAsync("role-grant", groupId, Guid.Empty, null, null, request, null, cancellationToken);

    public Task<GroupMutationResult> RevokeRoleAsync(Guid groupId, Guid assignmentId,
        RevokeGroupRoleRequest? request, CancellationToken cancellationToken = default) =>
        MutateAsync("role-revoke", groupId, assignmentId, null, null, null, request,
            cancellationToken);

    private async Task<GroupMutationResult> MutateAsync(string action, Guid groupId,
        Guid assignmentId, CreateGroupRequest? create, ChangeGroupMemberRequest? member,
        GrantGroupRoleRequest? grant, RevokeGroupRoleRequest? revoke,
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
            ("tenant", tenantId)) is not "active")
            return await DenyAsync(GroupMutationStatus.NotFound, "tenant-inactive");
        var current = await resolver.ResolveAsync(actorId, tenantId, cancellationToken);
        if (!current.Allowed)
            return await DenyAsync(GroupMutationStatus.NotFound, "membership-inactive");
        var actorPermissions = current.Permissions.ToHashSet(StringComparer.Ordinal);
        if (!actorPermissions.Contains("access.group.manage"))
            return await DenyAsync(GroupMutationStatus.Forbidden, "permission-missing");

        if (action == "create")
        {
            var name = create?.Name?.Trim();
            var description = create?.Description?.Trim();
            if (name is not { Length: >= 3 and <= 60 } || name.Any(char.IsControl) ||
                description is { Length: > 500 } || description?.Any(char.IsControl) == true)
                return await DenyAsync(GroupMutationStatus.Invalid, "invalid-group");
            if (await ScalarAsync("""
                SELECT EXISTS (SELECT 1 FROM groups WHERE tenant_id = @tenant
                  AND lower(name) = lower(@name))
                """, ("tenant", tenantId), ("name", name)) is true)
                return await DenyAsync(GroupMutationStatus.Conflict, "group-name-taken");
            var id = Guid.NewGuid();
            await ExecuteAsync("""
                INSERT INTO groups (id, tenant_id, name, description)
                VALUES (@id, @tenant, @name, @description)
                """, ("id", id), ("tenant", tenantId), ("name", name),
                ("description", (object?)description ?? DBNull.Value));
            await AuditAsync("access.group.created", "group", id.ToString("D"),
                "group-created", null, new { groupId = id });
            await BumpAsync();
            await transaction.CommitAsync(cancellationToken);
            return new(GroupMutationStatus.Created, id);
        }

        if (groupId == Guid.Empty || await ScalarAsync("""
            SELECT id FROM groups WHERE id = @group AND tenant_id = @tenant FOR UPDATE
            """, ("group", groupId), ("tenant", tenantId)) is null)
            return await DenyAsync(GroupMutationStatus.NotFound, "group-unavailable");

        if (action is "member-add" or "member-remove")
        {
            var target = member?.UserId ?? Guid.Empty;
            var reason = member?.Reason?.Trim();
            if (target == Guid.Empty || !AccessRoleGrantPolicy.ValidReasonCode(reason))
                return await DenyAsync(GroupMutationStatus.Invalid, "invalid-member-change");
            if (target == actorId)
                return await DenyAsync(GroupMutationStatus.Forbidden, "self-group-change");
            var exists = await ScalarAsync("""
                SELECT EXISTS (SELECT 1 FROM group_members
                  WHERE tenant_id = @tenant AND group_id = @group AND user_id = @user)
                """, ("tenant", tenantId), ("group", groupId), ("user", target)) is true;
            if (action == "member-add")
            {
                if (await ScalarAsync("""
                    SELECT EXISTS (SELECT 1 FROM tenant_members m
                      JOIN identities i ON i.user_id = m.user_id
                      WHERE m.tenant_id = @tenant AND m.user_id = @user
                        AND m.status = 'active' AND i.status = 'active')
                    """, ("tenant", tenantId), ("user", target)) is not true)
                    return await DenyAsync(GroupMutationStatus.NotFound, "member-unavailable");
                if (exists)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new(GroupMutationStatus.Unchanged);
                }
                var activeRoles = await ReadActiveGroupRolesAsync(groupId);
                foreach (var roleId in activeRoles)
                {
                    var role = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
                        tenantId, roleId, cancellationToken);
                    if (role is null)
                        return await DenyAsync(GroupMutationStatus.Conflict, "role-unavailable");
                    if (AccessRoleGrantPolicy.Check(role, actorPermissions, user.HasRecentMfa)
                        is { } decision)
                        return await DenyAsync(MapDecision(decision), "group-role-policy-denied");
                }
                await ExecuteAsync("""
                    INSERT INTO group_members (tenant_id, group_id, user_id)
                    VALUES (@tenant, @group, @user)
                    """, ("tenant", tenantId), ("group", groupId), ("user", target));
                await AuditAsync("access.group.member.added", "group-member", groupId.ToString("D"),
                    reason!, null, new { groupId, userId = target });
            }
            else
            {
                if (!exists)
                    return await DenyAsync(GroupMutationStatus.NotFound, "group-member-unavailable");
                if (await ScalarAsync("""
                    SELECT EXISTS (SELECT 1 FROM group_role_assignments gra
                      JOIN roles r ON r.id = gra.role_id
                      WHERE gra.tenant_id = @tenant AND gra.group_id = @group
                        AND gra.revoked_at IS NULL
                        AND (gra.expires_at IS NULL OR gra.expires_at > now())
                        AND r.name = 'owner')
                    """, ("tenant", tenantId), ("group", groupId)) is true)
                    return await DenyAsync(GroupMutationStatus.Conflict,
                        "ownership-transfer-required");
                await ExecuteAsync("""
                    DELETE FROM group_members
                    WHERE tenant_id = @tenant AND group_id = @group AND user_id = @user
                    """, ("tenant", tenantId), ("group", groupId), ("user", target));
                await AuditAsync("access.group.member.removed", "group-member", groupId.ToString("D"),
                    reason!, new { groupId, userId = target }, null);
            }
            await BumpAsync();
            await transaction.CommitAsync(cancellationToken);
            return new(GroupMutationStatus.Changed);
        }

        // A group role change would change the actor's own roles when they belong to the group.
        if (await ScalarAsync("""
            SELECT EXISTS (SELECT 1 FROM group_members
              WHERE tenant_id = @tenant AND group_id = @group AND user_id = @actor)
            """, ("tenant", tenantId), ("group", groupId), ("actor", actorId)) is true)
            return await DenyAsync(GroupMutationStatus.Forbidden, "self-group-role-change");

        var roleReason = (action == "role-grant" ? grant?.Reason : revoke?.Reason)?.Trim();
        if (!AccessRoleGrantPolicy.ValidReasonCode(roleReason))
            return await DenyAsync(GroupMutationStatus.Invalid, "invalid-reason");
        var validatedReason = roleReason!;
        GroupRoleState? previous = null;
        Guid roleToChange;
        DateTimeOffset? expiresAt;
        if (action == "role-grant")
        {
            if (grant is null || grant.RoleId == Guid.Empty ||
                grant.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                return await DenyAsync(GroupMutationStatus.Invalid, "invalid-grant");
            roleToChange = grant.RoleId;
            expiresAt = grant.ExpiresAt?.ToUniversalTime();
        }
        else
        {
            previous = await ReadGroupRoleAsync(groupId, assignmentId);
            if (previous is null)
                return await DenyAsync(GroupMutationStatus.NotFound, "group-role-unavailable");
            roleToChange = previous.RoleId;
            expiresAt = previous.ExpiresAt;
        }
        var selectedRole = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
            tenantId, roleToChange, cancellationToken);
        if (selectedRole is null)
            return await DenyAsync(GroupMutationStatus.NotFound, "role-unavailable");
        if (AccessRoleGrantPolicy.Check(selectedRole, actorPermissions, user.HasRecentMfa)
            is { } roleDecision)
            return await DenyAsync(MapDecision(roleDecision), "group-role-policy-denied");

        if (action == "role-grant")
        {
            var prior = await ReadActiveGroupRoleAsync(groupId, roleToChange);
            if (prior is not null)
            {
                if (prior.ExpiresAt is null || prior.ExpiresAt > DateTimeOffset.UtcNow)
                    return await DenyAsync(GroupMutationStatus.Conflict, "group-role-exists");
                await ExecuteAsync("""
                    UPDATE group_role_assignments SET revoked_at = now()
                    WHERE id = @id AND tenant_id = @tenant AND revoked_at IS NULL
                    """, ("id", prior.Id), ("tenant", tenantId));
                await AuditAsync("access.group.role.revoked", "group-role-assignment",
                    prior.Id.ToString("D"), "expired-grant-replaced", prior, null);
            }
            var assigned = new GroupRoleState(Guid.NewGuid(), groupId, roleToChange, expiresAt);
            await ExecuteAsync("""
                INSERT INTO group_role_assignments
                  (id, tenant_id, group_id, role_id, expires_at, granted_by)
                VALUES (@id, @tenant, @group, @role, @expires, @actor)
                """, ("id", assigned.Id), ("tenant", tenantId), ("group", groupId),
                ("role", roleToChange), ("expires", (object?)expiresAt ?? DBNull.Value),
                ("actor", actorId));
            await AuditAsync("access.group.role.granted", "group-role-assignment",
                assigned.Id.ToString("D"), validatedReason, null, assigned);
            await BumpAsync();
            await transaction.CommitAsync(cancellationToken);
            return new(GroupMutationStatus.Created, assigned.Id);
        }

        await ExecuteAsync("""
            UPDATE group_role_assignments SET revoked_at = now()
            WHERE id = @id AND tenant_id = @tenant AND group_id = @group
              AND revoked_at IS NULL
            """, ("id", assignmentId), ("tenant", tenantId), ("group", groupId));
        await AuditAsync("access.group.role.revoked", "group-role-assignment",
            assignmentId.ToString("D"), validatedReason, previous, null);
        await BumpAsync();
        await transaction.CommitAsync(cancellationToken);
        return new(GroupMutationStatus.Changed);

        async Task<GroupMutationResult> DenyAsync(GroupMutationStatus status, string code)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", $"access.group.{action}.denied", "group",
                groupId == Guid.Empty ? "new" : groupId.ToString("D"), code,
                cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(status);
        }

        async Task AuditAsync(string auditAction, string targetType, string targetId,
            string reason, object? before, object? after) =>
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", auditAction, targetType, targetId, reason, cancellationToken,
                beforeJson: before is null ? null : JsonSerializer.Serialize(before),
                afterJson: after is null ? null : JsonSerializer.Serialize(after), acr: user.Acr);

        async Task BumpAsync() =>
            await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction,
                tenantId, actorId, cancellationToken);

        async Task<Guid[]> ReadActiveGroupRolesAsync(Guid id)
        {
            await using var command = Command("""
                SELECT role_id FROM group_role_assignments
                WHERE tenant_id = @tenant AND group_id = @group AND revoked_at IS NULL
                  AND (expires_at IS NULL OR expires_at > now())
                """, ("tenant", tenantId), ("group", id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var ids = new List<Guid>();
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
            return ids.ToArray();
        }

        async Task<GroupRoleState?> ReadGroupRoleAsync(Guid id, Guid roleAssignmentId)
        {
            await using var command = Command("""
                SELECT id, group_id, role_id, expires_at FROM group_role_assignments
                WHERE id = @id AND group_id = @group AND tenant_id = @tenant
                  AND revoked_at IS NULL FOR UPDATE
                """, ("id", roleAssignmentId), ("group", id), ("tenant", tenantId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? new GroupRoleState(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)) : null;
        }

        async Task<GroupRoleState?> ReadActiveGroupRoleAsync(Guid id, Guid roleId)
        {
            await using var command = Command("""
                SELECT id, group_id, role_id, expires_at FROM group_role_assignments
                WHERE group_id = @group AND role_id = @role AND tenant_id = @tenant
                  AND revoked_at IS NULL FOR UPDATE
                """, ("group", id), ("role", roleId), ("tenant", tenantId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? new GroupRoleState(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)) : null;
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

    private static GroupMutationStatus MapDecision(RoleAssignmentStatus status) => status switch
    {
        RoleAssignmentStatus.Conflict => GroupMutationStatus.Conflict,
        RoleAssignmentStatus.Forbidden => GroupMutationStatus.Forbidden,
        RoleAssignmentStatus.StepUpRequired => GroupMutationStatus.StepUpRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private sealed record GroupRoleState(Guid Id, Guid GroupId, Guid RoleId,
        DateTimeOffset? ExpiresAt);
}
