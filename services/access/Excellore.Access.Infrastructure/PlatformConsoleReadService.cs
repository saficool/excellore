using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record PlatformPermissionView(string Key, string AppId, string Description,
    bool Privileged, bool OwnerOnly, bool Deprecated);
public sealed record PlatformAuditView(Guid Id, DateTimeOffset OccurredAt, string Action,
    string? TargetType, string? TargetId, string? Reason, Guid? ActorSub);
public sealed record PlatformSupportRoleView(Guid Id, string Name);
public sealed record PlatformSystemRoleView(Guid Id, string Name, string? Description);

/// <summary>Reads only platform metadata and platform-scope audit rows.</summary>
public sealed class PlatformConsoleReadService(NpgsqlDataSource database)
{
    public async Task<IReadOnlyList<PlatformPermissionView>> PermissionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT key,app_id,description,privileged,owner_only,deprecated
            FROM permissions ORDER BY app_id,key
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PlatformPermissionView>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        return result;
    }

    public async Task<IReadOnlyList<PlatformAuditView>> AuditAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('app.scope','platform',true)", connection, tx))
            await scope.ExecuteNonQueryAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id,occurred_at,action,target_type,target_id,reason,actor_sub
            FROM audit_events WHERE tenant_id IS NULL
            ORDER BY occurred_at DESC,id DESC LIMIT 200
            """, connection, tx);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PlatformAuditView>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6)));
        return result;
    }

    public async Task<IReadOnlyList<PlatformSupportRoleView>> SupportRolesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('app.scope','platform',true)", connection, tx))
            await scope.ExecuteNonQueryAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id,name FROM roles WHERE tenant_id IS NULL AND is_system
              AND name IN ('viewer','auditor') AND deleted_at IS NULL ORDER BY name
            """, connection, tx);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PlatformSupportRoleView>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetString(1)));
        return result;
    }

    public async Task<IReadOnlyList<PlatformSystemRoleView>> SystemRolesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('app.scope','platform',true)", connection, tx))
            await scope.ExecuteNonQueryAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id,name,description FROM roles WHERE tenant_id IS NULL
              AND is_system AND deleted_at IS NULL ORDER BY name
            """, connection, tx);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PlatformSystemRoleView>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        return result;
    }
}
