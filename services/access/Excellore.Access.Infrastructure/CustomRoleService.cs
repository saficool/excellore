using System.Text.Json;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record CustomRoleRequest(string? Name, string? Description, string[]? Permissions);

public enum CustomRoleStatus
{
    Created, Updated, Deleted, Unchanged, Invalid, Forbidden, StepUpRequired,
    NotFound, Conflict, LimitReached, InUse, ConfigurationUnavailable
}

public sealed record CustomRoleResult(CustomRoleStatus Status, Guid? RoleId = null);

public sealed class CustomRoleService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext, ICurrentUser user, AccessResolutionService resolver)
{
    public Task<CustomRoleResult> CreateAsync(CustomRoleRequest? request,
        CancellationToken cancellationToken = default) =>
        MutateAsync("create", Guid.Empty, request, cancellationToken);

    public Task<CustomRoleResult> UpdateAsync(Guid roleId, CustomRoleRequest? request,
        CancellationToken cancellationToken = default) =>
        MutateAsync("update", roleId, request, cancellationToken);

    public Task<CustomRoleResult> DeleteAsync(Guid roleId,
        CancellationToken cancellationToken = default) =>
        MutateAsync("delete", roleId, null, cancellationToken);

    private async Task<CustomRoleResult> MutateAsync(string action, Guid roleId,
        CustomRoleRequest? request, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId;
        var actorId = user.UserId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(@key)",
            cancellationToken, ("key", AccessAuditWriter.AuditLock));
        await ExecuteAsync(connection, transaction, "SELECT set_config('app.tenant_id', @tenant, true)",
            cancellationToken, ("tenant", tenantId.ToString("D")));
        var status = await ScalarAsync(connection, transaction,
            "SELECT status FROM tenants WHERE id = @tenant FOR UPDATE", cancellationToken,
            ("tenant", tenantId)) as string;
        if (status != "active")
            return await DenyAsync(CustomRoleStatus.NotFound, "tenant-inactive");

        var current = await resolver.ResolveAsync(actorId, tenantId, cancellationToken);
        if (!current.Allowed)
            return await DenyAsync(CustomRoleStatus.NotFound, "membership-inactive");
        var actorPermissions = current.Permissions.ToHashSet(StringComparer.Ordinal);
        if (!actorPermissions.Contains("access.role.manage"))
            return await DenyAsync(CustomRoleStatus.Forbidden, "permission-missing");
        if (!user.HasRecentMfa)
            return await DenyAsync(CustomRoleStatus.StepUpRequired, "step-up-required");

        RoleState? existing = null;
        if (action != "create")
        {
            existing = await ReadRoleAsync(connection, transaction, roleId, tenantId, cancellationToken);
            if (existing is null)
                return await DenyAsync(CustomRoleStatus.NotFound, "role-unavailable");
        }

        if (action == "delete")
        {
            var inUse = await ScalarAsync(connection, transaction, """
                SELECT EXISTS (
                    SELECT 1 FROM role_assignments
                    WHERE tenant_id = @tenant AND role_id = @role AND revoked_at IS NULL
                    UNION ALL
                    SELECT 1 FROM group_role_assignments
                    WHERE tenant_id = @tenant AND role_id = @role AND revoked_at IS NULL)
                """, cancellationToken, ("tenant", tenantId), ("role", roleId));
            if (inUse is true)
                return await DenyAsync(CustomRoleStatus.InUse, "role-in-use");
            await ExecuteAsync(connection, transaction,
                "UPDATE roles SET deleted_at = now(), updated_at = now() WHERE id = @role AND tenant_id = @tenant AND NOT is_system AND deleted_at IS NULL",
                cancellationToken, ("role", roleId), ("tenant", tenantId));
            await RecordChangeAsync("deleted", roleId, existing!, null);
            await transaction.CommitAsync(cancellationToken);
            return new(CustomRoleStatus.Deleted, roleId);
        }

        if (!TryNormalize(request, out var name, out var description, out var keys))
            return await DenyAsync(CustomRoleStatus.Invalid, "invalid-role-request");
        if (await ScalarAsync(connection, transaction, """
            SELECT EXISTS (SELECT 1 FROM roles
              WHERE (tenant_id = @tenant OR tenant_id IS NULL) AND deleted_at IS NULL
                AND lower(name) = lower(@name) AND id <> @role)
            """, cancellationToken, ("tenant", tenantId), ("name", name!),
            ("role", roleId)) is true)
            return await DenyAsync(CustomRoleStatus.Conflict, "role-name-taken");

        if (action == "create")
        {
            var limit = await ReadRoleLimitAsync(connection, transaction, tenantId, cancellationToken);
            if (limit is null)
                return await DenyAsync(CustomRoleStatus.ConfigurationUnavailable, "role-limit-unavailable");
            var count = (long)(await ScalarAsync(connection, transaction,
                "SELECT count(*) FROM roles WHERE tenant_id = @tenant AND NOT is_system AND deleted_at IS NULL",
                cancellationToken, ("tenant", tenantId)) ?? 0L);
            if (count >= limit.Value)
                return await DenyAsync(CustomRoleStatus.LimitReached, "role-limit-reached");
        }

        var flags = await ReadPermissionFlagsAsync(connection, transaction, keys!, tenantId,
            cancellationToken);
        if (flags.Count != keys!.Length || flags.Values.Any(flag =>
                flag.OwnerOnly || flag.Deprecated || !flag.Enabled))
            return await DenyAsync(CustomRoleStatus.Invalid, "role-content-forbidden");
        if (keys.Any(key => !actorPermissions.Contains(key)))
            return await DenyAsync(CustomRoleStatus.Forbidden, "role-permission-escalation");
        var privileged = flags.Values.Any(flag => flag.Privileged);
        var after = new RoleState(roleId == Guid.Empty ? Guid.NewGuid() : roleId,
            name!, description, privileged, keys);
        if (existing is not null && existing.Name == after.Name &&
            existing.Description == after.Description && existing.Privileged == after.Privileged &&
            existing.Permissions.SequenceEqual(after.Permissions, StringComparer.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(CustomRoleStatus.Unchanged, roleId);
        }

        if (action == "create")
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO roles (id, tenant_id, name, description, is_system, privileged, created_by)
                VALUES (@id, @tenant, @name, @description, false, @privileged, @actor)
                """, cancellationToken, ("id", after.Id), ("tenant", tenantId),
                ("name", after.Name), ("description", (object?)after.Description ?? DBNull.Value),
                ("privileged", privileged), ("actor", actorId));
        }
        else
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE roles SET name = @name, description = @description,
                  privileged = @privileged, updated_at = now()
                WHERE id = @id AND tenant_id = @tenant AND NOT is_system
                """, cancellationToken, ("id", after.Id), ("tenant", tenantId),
                ("name", after.Name), ("description", (object?)after.Description ?? DBNull.Value),
                ("privileged", privileged));
            await ExecuteAsync(connection, transaction,
                "DELETE FROM role_permissions WHERE role_id = @role AND tenant_id = @tenant",
                cancellationToken, ("role", after.Id), ("tenant", tenantId));
        }
        foreach (var key in keys)
            await ExecuteAsync(connection, transaction, """
                INSERT INTO role_permissions (role_id, tenant_id, permission_key)
                VALUES (@role, @tenant, @key)
                """, cancellationToken, ("role", after.Id), ("tenant", tenantId), ("key", key));
        await RecordChangeAsync(action == "create" ? "created" : "updated", after.Id,
            existing, after);
        await transaction.CommitAsync(cancellationToken);
        return new(action == "create" ? CustomRoleStatus.Created : CustomRoleStatus.Updated, after.Id);

        async Task<CustomRoleResult> DenyAsync(CustomRoleStatus denied, string reason)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", $"access.role.{action}.denied", "role",
                roleId == Guid.Empty ? "new" : roleId.ToString("D"), reason,
                cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(denied);
        }

        async Task RecordChangeAsync(string verb, Guid targetId, RoleState? before, RoleState? afterState)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", $"access.role.{verb}", "role", targetId.ToString("D"),
                $"custom role {verb}", cancellationToken,
                beforeJson: before is null ? null : JsonSerializer.Serialize(before),
                afterJson: afterState is null ? null : JsonSerializer.Serialize(afterState),
                acr: user.Acr);
            await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction,
                tenantId, actorId, cancellationToken);
            await AccessAuthzChangeWriter.WriteRoleEventAsync(connection, transaction,
                tenantId, targetId, actorId, verb, cancellationToken);
        }
    }

    private static bool TryNormalize(CustomRoleRequest? request, out string? name,
        out string? description, out string[]? keys)
    {
        name = request?.Name?.Trim();
        description = request?.Description?.Trim();
        keys = request?.Permissions?.Order(StringComparer.Ordinal).ToArray();
        return name is { Length: >= 3 and <= 60 } &&
            !name.Any(char.IsControl) &&
            (description is null || description.Length <= 500 && !description.Any(char.IsControl)) &&
            keys is { Length: <= 100 } &&
            keys.All(key => !string.IsNullOrWhiteSpace(key) && key.Length <= 120) &&
            keys.Distinct(StringComparer.Ordinal).Count() == keys.Length;
    }

    private static async Task<int?> ReadRoleLimitAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, CancellationToken cancellationToken)
    {
        var json = await ScalarAsync(connection, transaction, """
            SELECT features::text FROM tenant_apps
            WHERE tenant_id = @tenant AND app_id = 'access' AND status = 'enabled'
            """, cancellationToken, ("tenant", tenantId)) as string;
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (!document.RootElement.TryGetProperty("maxCustomRoles", out var value)) return 25;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var limit) &&
            limit >= 0 ? Math.Min(limit, 25) : null;
    }

    private static async Task<Dictionary<string, PermissionFlags>> ReadPermissionFlagsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string[] keys, Guid tenantId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PermissionFlags>(StringComparer.Ordinal);
        if (keys.Length == 0) return result;
        await using var command = new NpgsqlCommand("""
            SELECT p.key, p.owner_only, p.deprecated, p.privileged,
              COALESCE(a.core OR ta.status = 'enabled', false)
            FROM permissions p
            JOIN apps a ON a.id = p.app_id
            LEFT JOIN tenant_apps ta ON ta.tenant_id = @tenant AND ta.app_id = p.app_id
            WHERE p.key = ANY(@keys)
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("keys", keys);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0), new PermissionFlags(reader.GetBoolean(1),
                reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        return result;
    }

    private static async Task<RoleState?> ReadRoleAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid roleId, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.name, r.description, r.privileged, rp.permission_key
            FROM roles r LEFT JOIN role_permissions rp ON rp.role_id = r.id
            WHERE r.id = @role AND r.tenant_id = @tenant AND NOT r.is_system
              AND r.deleted_at IS NULL
            ORDER BY rp.permission_key FOR UPDATE OF r
            """, connection, transaction);
        command.Parameters.AddWithValue("role", roleId);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        string? name = null;
        string? description = null;
        var privileged = false;
        var keys = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            name ??= reader.GetString(0);
            description ??= reader.IsDBNull(1) ? null : reader.GetString(1);
            privileged = reader.GetBoolean(2);
            if (!reader.IsDBNull(3)) keys.Add(reader.GetString(3));
        }
        return name is null ? null : new RoleState(roleId, name, description, privileged, keys.ToArray());
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private sealed record RoleState(Guid Id, string Name, string? Description,
        bool Privileged, string[] Permissions);
    private sealed record PermissionFlags(bool OwnerOnly, bool Deprecated, bool Privileged, bool Enabled);
}
