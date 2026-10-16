using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record MemberLookupRequest(Guid UserId);
public sealed record TenantMemberView(Guid UserId, Guid MembershipId, string Status,
    string Email, string? GivenName, string? FamilyName, DateTimeOffset? JoinedAt);
public sealed record TenantAppView(string Id, string Name);
public sealed record TenantRoleAssignmentView(Guid Id, Guid UserId, Guid RoleId,
    string RoleName, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt);
public sealed record TenantAuditView(Guid Id, DateTimeOffset OccurredAt, Guid? ActorSub,
    string ActorType, string Action, string? TargetType, string? TargetId,
    string? Reason, string? Acr);

public sealed class AccessTenantReadService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext)
{
    public async Task<IReadOnlyList<TenantMemberView>> ListMembersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<TenantMemberView>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT m.user_id, m.membership_id, m.status, i.email,
                   i.given_name, i.family_name, m.joined_at
            FROM tenant_members m JOIN identities i ON i.user_id = m.user_id
            WHERE m.tenant_id = @tenant AND m.status <> 'removed' AND NOT m.is_support
            ORDER BY m.created_at, m.user_id LIMIT 500
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadMember(reader));
        return result;
    }

    public async Task<TenantMemberView?> GetMemberAsync(Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT m.user_id, m.membership_id, m.status, i.email,
                   i.given_name, i.family_name, m.joined_at
            FROM tenant_members m JOIN identities i ON i.user_id = m.user_id
            WHERE m.tenant_id = @tenant AND m.user_id = @user
              AND m.status <> 'removed' AND NOT m.is_support
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMember(reader) : null;
    }

    public async Task<IReadOnlyList<TenantAppView>> ListAppsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<TenantAppView>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.id, a.name FROM apps a
            LEFT JOIN tenant_apps ta ON ta.app_id = a.id AND ta.tenant_id = @tenant
            WHERE a.core OR ta.status = 'enabled' ORDER BY a.id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new TenantAppView(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public async Task<IReadOnlyList<TenantRoleAssignmentView>> ListRoleAssignmentsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<TenantRoleAssignmentView>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.id, a.user_id, a.role_id, r.name, a.expires_at, a.created_at
            FROM role_assignments a JOIN roles r ON r.id = a.role_id
            WHERE a.tenant_id = @tenant AND a.revoked_at IS NULL
              AND (a.expires_at IS NULL OR a.expires_at > now())
            ORDER BY a.created_at DESC, a.id DESC LIMIT 500
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new TenantRoleAssignmentView(reader.GetGuid(0), reader.GetGuid(1),
                reader.GetGuid(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        return result;
    }

    public async Task<IReadOnlyList<TenantAuditView>> ListAuditAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<TenantAuditView>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, occurred_at, actor_sub, actor_type, action, target_type,
                   target_id, reason, acr
            FROM audit_events WHERE tenant_id = @tenant
            ORDER BY occurred_at DESC, id DESC LIMIT 100
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new TenantAuditView(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        return result;
    }

    private async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TenantMemberView ReadMember(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
}
