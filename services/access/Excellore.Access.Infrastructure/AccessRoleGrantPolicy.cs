using Npgsql;

namespace Excellore.Access.Infrastructure;

internal sealed record GrantablePermission(string Key, bool Privileged, bool Deprecated, bool Enabled);

internal sealed record GrantableRole(string Name, bool Privileged,
    IReadOnlyList<GrantablePermission> Permissions);

internal static class AccessRoleGrantPolicy
{
    internal static bool ValidReasonCode(string? reason) =>
        reason is { Length: >= 1 and <= 64 } && reason[0] is >= 'a' and <= 'z' &&
        reason.Skip(1).All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'
            or '.' or '_' or '-');

    internal static RoleAssignmentStatus? Check(GrantableRole role,
        IReadOnlySet<string> actorPermissions, bool hasRecentMfa)
    {
        if (role.Name == "owner") return RoleAssignmentStatus.Conflict;
        if (role.Permissions.Any(permission => !permission.Enabled || permission.Deprecated))
            return RoleAssignmentStatus.Conflict;
        if (role.Permissions.Any(permission => !actorPermissions.Contains(permission.Key)))
            return RoleAssignmentStatus.Forbidden;
        if ((role.Privileged || role.Permissions.Any(permission => permission.Privileged)) &&
            !hasRecentMfa)
            return RoleAssignmentStatus.StepUpRequired;
        return null;
    }

    internal static async Task<GrantableRole?> ReadAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, Guid roleId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.name, r.privileged, p.key, p.privileged, p.deprecated,
              COALESCE(a.core OR ta.status = 'enabled', false)
            FROM roles r
            LEFT JOIN role_permissions rp ON rp.role_id = r.id
            LEFT JOIN permissions p ON p.key = rp.permission_key
            LEFT JOIN apps a ON a.id = p.app_id
            LEFT JOIN tenant_apps ta ON ta.tenant_id = @tenant AND ta.app_id = a.id
            WHERE r.id = @role AND (r.tenant_id IS NULL OR r.tenant_id = @tenant)
              AND r.deleted_at IS NULL
            ORDER BY p.key
            """, connection, transaction);
        command.Parameters.AddWithValue("role", roleId);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        string? name = null;
        var privileged = false;
        var permissions = new List<GrantablePermission>();
        while (await reader.ReadAsync(cancellationToken))
        {
            name ??= reader.GetString(0);
            privileged = reader.GetBoolean(1);
            if (!reader.IsDBNull(2))
                permissions.Add(new GrantablePermission(reader.GetString(2), reader.GetBoolean(3),
                    reader.GetBoolean(4), reader.GetBoolean(5)));
        }
        return name is null ? null : new GrantableRole(name, privileged, permissions);
    }
}
