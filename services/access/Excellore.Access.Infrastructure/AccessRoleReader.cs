using Excellore.Platform;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record ReadableRole(Guid Id, string Name, string? Description,
    bool IsSystem, bool Privileged, string[] Permissions);

public sealed record ReadablePermission(string Key, string App, string Description,
    bool Privileged, bool OwnerOnly);

public sealed class AccessRoleReader(NpgsqlDataSource dataSource, ITenantIdAccessor tenantContext)
{
    public Task<IReadOnlyList<ReadableRole>> ListRolesAsync(CancellationToken cancellationToken = default) =>
        InTenantAsync(async (connection, transaction) =>
        {
            var rows = new Dictionary<Guid, RoleBuilder>();
            await using var command = new NpgsqlCommand("""
                SELECT r.id, r.name, r.description, r.is_system, r.privileged, rp.permission_key
                FROM roles r
                LEFT JOIN role_permissions rp ON rp.role_id = r.id
                WHERE r.deleted_at IS NULL
                ORDER BY r.is_system DESC, r.name, rp.permission_key
                """, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (!rows.TryGetValue(id, out var role))
                {
                    role = new RoleBuilder(id, reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetBoolean(3), reader.GetBoolean(4));
                    rows.Add(id, role);
                }
                if (!reader.IsDBNull(5)) role.Permissions.Add(reader.GetString(5));
            }
            return (IReadOnlyList<ReadableRole>)rows.Values
                .Select(role => new ReadableRole(role.Id, role.Name, role.Description,
                    role.IsSystem, role.Privileged, role.Permissions.ToArray()))
                .ToArray();
        }, cancellationToken);

    public async Task<ReadableRole?> GetRoleAsync(Guid roleId,
        CancellationToken cancellationToken = default) =>
        (await ListRolesAsync(cancellationToken)).FirstOrDefault(role => role.Id == roleId);

    public Task<IReadOnlyList<ReadablePermission>> ListPermissionsAsync(
        CancellationToken cancellationToken = default) =>
        InTenantAsync(async (connection, transaction) =>
        {
            var result = new List<ReadablePermission>();
            await using var command = new NpgsqlCommand("""
                SELECT p.key, p.app_id, p.description, p.privileged, p.owner_only
                FROM permissions p
                JOIN apps a ON a.id = p.app_id
                LEFT JOIN tenant_apps ta ON ta.app_id = a.id AND ta.tenant_id = @tenant
                WHERE NOT p.deprecated AND (a.core OR ta.status = 'enabled')
                ORDER BY p.app_id, p.key
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant", tenantContext.TenantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new ReadablePermission(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4)));
            return (IReadOnlyList<ReadablePermission>)result;
        }, cancellationToken);

    private async Task<T> InTenantAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> read,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var result = await read(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private sealed record RoleBuilder(Guid Id, string Name, string? Description,
        bool IsSystem, bool Privileged)
    {
        public List<string> Permissions { get; } = [];
    }
}
