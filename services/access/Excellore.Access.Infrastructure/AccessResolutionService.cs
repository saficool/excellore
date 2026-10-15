using System.Data;
using Excellore.Access.Application;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record ResolveRequest(Guid UserId, Guid TenantId);
public sealed record TenantListRequest(Guid UserId);
public sealed record ResolveSlugRequest(Guid UserId, string? Slug);

public sealed record AccessResolution(Guid TenantId, bool Allowed, string[] Roles,
    string[] Permissions, string[] Apps, long Version, DateTimeOffset ResolvedAt)
{
    public static AccessResolution Denied(Guid tenantId) =>
        new(tenantId, false, [], [], [], 0, DateTimeOffset.UtcNow);
}

public sealed record ListedTenant(Guid TenantId, string Slug, string Name, string Status,
    string[] Roles, string[] Apps);

public sealed record ResolvedTenant(Guid TenantId, string Slug, string Name);

public sealed class AccessResolutionService(NpgsqlDataSource dataSource)
{
    public async Task<AccessResolution> ResolveAsync(Guid userId, Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || tenantId == Guid.Empty) return AccessResolution.Denied(tenantId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using (var command = Command(connection, transaction,
            "SELECT set_config('app.tenant_id', @tenant, true)", ("tenant", tenantId.ToString("D"))))
            await command.ExecuteNonQueryAsync(cancellationToken);

        var grants = new HashSet<RoleGrant>();
        var rolePermissions = new HashSet<RolePermission>();
        string[] enabledApps = [];
        string? tenantStatus = null;
        string? membershipStatus = null;
        string? identityStatus = null;
        bool isSupport = false;
        DateTimeOffset? membershipExpiresAt = null;
        string? supportStatus = null;
        DateTimeOffset? grantExpiresAt = null;
        Guid? supportGrantRoleId = null;
        long version = 0;
        await using (var command = Command(connection, transaction, """
            WITH grants AS (
              SELECT r.id AS role_id, r.name, ra.expires_at, ra.revoked_at
              FROM role_assignments ra JOIN roles r ON r.id = ra.role_id AND r.deleted_at IS NULL
              WHERE ra.tenant_id = @tenant AND ra.user_id = @user
                AND (r.tenant_id IS NULL OR r.tenant_id = @tenant)
              UNION ALL
              SELECT r.id, r.name, gra.expires_at, gra.revoked_at
              FROM group_members gm
              JOIN group_role_assignments gra ON gra.tenant_id = gm.tenant_id AND gra.group_id = gm.group_id
              JOIN roles r ON r.id = gra.role_id AND r.deleted_at IS NULL
              WHERE gm.tenant_id = @tenant AND gm.user_id = @user
                AND (r.tenant_id IS NULL OR r.tenant_id = @tenant)
            )
            SELECT t.status, t.authz_version, m.status, i.status,
              gr.role_id, gr.name, gr.expires_at, gr.revoked_at, p.key, p.app_id,
              ARRAY(SELECT a.id FROM apps a LEFT JOIN tenant_apps ta
                ON ta.app_id = a.id AND ta.tenant_id = @tenant
                WHERE a.core OR ta.status = 'enabled') AS enabled_apps,
              m.is_support, m.expires_at, sg.status, sg.expires_at, sg.role_id
            FROM tenants t
            LEFT JOIN tenant_members m ON m.tenant_id = t.id AND m.user_id = @user
            LEFT JOIN identities i ON i.user_id = m.user_id
            LEFT JOIN support_grants sg ON sg.id = m.support_grant_id
              AND sg.tenant_id = t.id AND sg.staff_user_id = m.user_id
            LEFT JOIN grants gr ON true
            LEFT JOIN role_permissions rp ON rp.role_id = gr.role_id
            LEFT JOIN permissions p ON p.key = rp.permission_key
            WHERE t.id = @tenant
            """, ("tenant", tenantId), ("user", userId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tenantStatus ??= reader.GetString(0);
                version = reader.GetInt64(1);
                membershipStatus ??= reader.IsDBNull(2) ? null : reader.GetString(2);
                identityStatus ??= reader.IsDBNull(3) ? null : reader.GetString(3);
                enabledApps = reader.GetFieldValue<string[]>(10);
                isSupport = !reader.IsDBNull(11) && reader.GetBoolean(11);
                membershipExpiresAt = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12);
                supportStatus = reader.IsDBNull(13) ? null : reader.GetString(13);
                grantExpiresAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14);
                supportGrantRoleId = reader.IsDBNull(15) ? null : reader.GetGuid(15);
                if (reader.IsDBNull(4)) continue;
                var roleId = reader.GetGuid(4);
                grants.Add(new RoleGrant(roleId, reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
                if (!reader.IsDBNull(8))
                    rolePermissions.Add(new RolePermission(roleId, reader.GetString(8), reader.GetString(9)));
            }
        }
        if (tenantStatus != "active" || membershipStatus != "active" || identityStatus != "active")
            return AccessResolution.Denied(tenantId);
        var now = DateTimeOffset.UtcNow;
        if (isSupport && (supportStatus != "active" || membershipExpiresAt is null ||
            grantExpiresAt is null || supportGrantRoleId is null ||
            membershipExpiresAt <= now || grantExpiresAt <= now))
            return AccessResolution.Denied(tenantId);
        if (isSupport)
        {
            grants.RemoveWhere(grant => grant.RoleId != supportGrantRoleId ||
                grant.Name is not ("viewer" or "auditor"));
            rolePermissions.RemoveWhere(permission => permission.RoleId != supportGrantRoleId);
        }
        var effective = EffectivePermissionCalculator.Resolve(true, true, grants, rolePermissions, enabledApps, now);
        if (isSupport)
        {
            var readPermissions = new HashSet<string>(StringComparer.Ordinal)
            {
                "access.app.access", "access.member.read", "access.role.read",
                "access.apps.read", "access.audit.read", "access.settings.read"
            };
            var permitted = effective.Permissions.Where(readPermissions.Contains).ToArray();
            if (!permitted.Contains("access.app.access", StringComparer.Ordinal))
                return AccessResolution.Denied(tenantId);
            await transaction.CommitAsync(cancellationToken);
            return new AccessResolution(tenantId, true, effective.Roles,
                permitted, ["access", "shell"], version, now);
        }
        await transaction.CommitAsync(cancellationToken);
        return new AccessResolution(tenantId, effective.Allowed, effective.Roles,
            effective.Permissions, effective.Apps, version, now);
    }

    public async Task<IReadOnlyList<ListedTenant>> ListTenantsAsync(Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) return [];
        var candidates = new List<(Guid Id, string Slug, string Name)>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand("""
            SELECT t.id, t.slug, t.name FROM user_tenant_index i
            JOIN tenants t ON t.id = i.tenant_id
            WHERE i.user_id = @user AND i.membership_status = 'active' AND t.status = 'active'
            ORDER BY t.slug
            """, connection))
        {
            command.Parameters.AddWithValue("user", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }

        var result = new List<ListedTenant>();
        foreach (var candidate in candidates)
        {
            var resolution = await ResolveAsync(userId, candidate.Id, cancellationToken);
            if (resolution.Allowed)
                result.Add(new ListedTenant(candidate.Id, candidate.Slug, candidate.Name,
                    "active", resolution.Roles, resolution.Apps));
        }
        return result;
    }

    public async Task<ResolvedTenant?> ResolveSlugAsync(Guid userId, string? slug,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || slug is not { Length: >= 3 and <= 40 } ||
            !System.Text.RegularExpressions.Regex.IsMatch(slug,
                "^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return null;
        Guid tenantId;
        string name;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(
            "SELECT id, name FROM tenants WHERE slug = @slug AND status = 'active'", connection))
        {
            command.Parameters.AddWithValue("slug", slug);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            tenantId = reader.GetGuid(0);
            name = reader.GetString(1);
        }
        return (await ResolveAsync(userId, tenantId, cancellationToken)).Allowed
            ? new ResolvedTenant(tenantId, slug, name)
            : null;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
