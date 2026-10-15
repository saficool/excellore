using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Excellore.Access.Application;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public enum CatalogSyncResult { Updated, Unchanged, Invalid, Conflict, UnknownApp }

public sealed class CatalogSyncService(NpgsqlDataSource dataSource)
{
    private const long CatalogLock = 0x4558434154414C4F;

    public async Task<CatalogSyncResult> SyncAsync(string app, PermissionCatalog? catalog,
        Guid actorSub, CancellationToken cancellationToken = default)
    {
        if (!PermissionCatalogValidator.IsValid(catalog, app)) return CatalogSyncResult.Invalid;
        var permissions = catalog!.Permissions!;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction,
            "SELECT pg_advisory_xact_lock(@key)", cancellationToken, ("key", CatalogLock));
        await ExecuteAsync(connection, transaction,
            "SELECT set_config('app.scope', 'platform', true)", cancellationToken);

        if (await ScalarAsync(connection, transaction,
            "SELECT count(*) FROM apps WHERE id = @app", cancellationToken, ("app", app)) == 0)
            return CatalogSyncResult.UnknownApp;

        var existing = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Command(connection, transaction,
            "SELECT key FROM permissions WHERE app_id = @app", ("app", app)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(0));

        var incoming = permissions.Select(permission => permission.Key!).ToHashSet(StringComparer.Ordinal);
        foreach (var key in incoming)
        {
            await using var ownerCommand = Command(connection, transaction,
                "SELECT app_id FROM permissions WHERE key = @key", ("key", key));
            var owner = await ownerCommand.ExecuteScalarAsync(cancellationToken) as string;
            if (owner is not null && owner != app) return CatalogSyncResult.Conflict;
        }
        // Runtime RLS cannot prove the absence of custom-role grants in every
        // tenant. Deletion requires a separate reviewed migration.
        if (existing.Except(incoming, StringComparer.Ordinal).Any()) return CatalogSyncResult.Conflict;
        var changed = false;

        var roleIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var name in PermissionCatalogValidator.RoleNames.Order(StringComparer.Ordinal))
        {
            var id = await GuidScalarAsync(connection, transaction,
                "SELECT id FROM roles WHERE tenant_id IS NULL AND is_system AND name = @name",
                cancellationToken, ("name", name));
            if (id is null)
            {
                id = Guid.NewGuid();
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO roles (id, tenant_id, name, is_system) VALUES (@id, NULL, @name, true)",
                    cancellationToken, ("id", id.Value), ("name", name));
                changed = true;
            }
            roleIds.Add(name, id.Value);
        }

        foreach (var permission in permissions)
        {
            var key = permission.Key!;
            var updated = await ExecuteAsync(connection, transaction, """
                INSERT INTO permissions (key, app_id, description, privileged, owner_only, deprecated)
                VALUES (@key, @app, @description, @privileged, @owner_only, @deprecated)
                ON CONFLICT (key) DO UPDATE SET description = EXCLUDED.description,
                    privileged = EXCLUDED.privileged, owner_only = EXCLUDED.owner_only,
                    deprecated = EXCLUDED.deprecated, updated_at = now()
                WHERE permissions.app_id = EXCLUDED.app_id AND
                    (permissions.description, permissions.privileged, permissions.owner_only, permissions.deprecated)
                    IS DISTINCT FROM
                    (EXCLUDED.description, EXCLUDED.privileged, EXCLUDED.owner_only, EXCLUDED.deprecated)
                """, cancellationToken, ("key", key), ("app", app),
                ("description", permission.Description!), ("privileged", permission.Privileged),
                ("owner_only", permission.OwnerOnly), ("deprecated", permission.Deprecated));
            changed |= updated > 0;

            var desired = (permission.SystemRoles ?? []).ToHashSet(StringComparer.Ordinal);
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            await using (var command = Command(connection, transaction, """
                SELECT r.name FROM role_permissions rp JOIN roles r ON r.id = rp.role_id
                WHERE rp.permission_key = @key AND r.tenant_id IS NULL AND r.is_system
                """, ("key", key)))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) assigned.Add(reader.GetString(0));

            foreach (var name in assigned.Except(desired, StringComparer.Ordinal))
            {
                await ExecuteAsync(connection, transaction,
                    "DELETE FROM role_permissions WHERE role_id = @role AND permission_key = @key",
                    cancellationToken, ("role", roleIds[name]), ("key", key));
                changed = true;
            }
            foreach (var name in desired.Except(assigned, StringComparer.Ordinal))
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO role_permissions (role_id, tenant_id, permission_key) VALUES (@role, NULL, @key)",
                    cancellationToken, ("role", roleIds[name]), ("key", key));
                changed = true;
            }
        }

        if (changed)
        {
            await WriteAuditAsync(connection, transaction, actorSub, app, cancellationToken);
            await BumpTenantVersionsAsync(connection, transaction, actorSub, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return changed ? CatalogSyncResult.Updated : CatalogSyncResult.Unchanged;
    }

    private static async Task WriteAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid actorSub, string app, CancellationToken cancellationToken)
    {
        byte[]? previous;
        await using (var command = Command(connection, transaction,
            "SELECT hash FROM audit_events WHERE tenant_id IS NULL ORDER BY occurred_at DESC, id DESC LIMIT 1"))
            previous = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        var id = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        const string reason = "permission catalog deployment";
        var record = Encoding.UTF8.GetBytes($"{id:D}|{occurredAt:O}|{actorSub:D}|access.catalog.synced|app|{app}|{reason}");
        var input = new byte[(previous?.Length ?? 0) + record.Length];
        previous?.CopyTo(input, 0);
        record.CopyTo(input, previous?.Length ?? 0);
        var hash = SHA256.HashData(input);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO audit_events
              (id, tenant_id, occurred_at, actor_sub, actor_type, action, target_type, target_id, reason, prev_hash, hash)
            VALUES (@id, NULL, @occurred, @actor, 'service', 'access.catalog.synced', 'app', @app, @reason, @previous, @hash)
            """, cancellationToken, ("id", id), ("occurred", occurredAt),
            ("actor", actorSub), ("app", app), ("reason", reason),
            ("previous", (object?)previous ?? DBNull.Value), ("hash", hash));
    }

    private static async Task BumpTenantVersionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid actorSub, CancellationToken cancellationToken)
    {
        var tenants = new List<(Guid Id, long Version)>();
        await using (var command = Command(connection, transaction,
            "UPDATE tenants SET authz_version = authz_version + 1, updated_at = now() RETURNING id, authz_version"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) tenants.Add((reader.GetGuid(0), reader.GetInt64(1)));

        var correlationId = Guid.NewGuid();
        foreach (var tenant in tenants)
        {
            var id = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(new
            {
                specversion = "1.0", id, type = "access.authz.changed.v1", source = "excellore/access",
                time = DateTimeOffset.UtcNow, tenantid = tenant.Id, correlationid = correlationId,
                actor = new { sub = actorSub.ToString("D"), type = "machine" },
                data = new { tenantId = tenant.Id, authzVersion = tenant.Version }
            });
            await ExecuteAsync(connection, transaction,
                "INSERT INTO outbox (id, type, tenant_id, payload) VALUES (@id, 'access.authz.changed.v1', @tenant, @payload::jsonb)",
                cancellationToken, ("id", id), ("tenant", tenant.Id), ("payload", payload));
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task<Guid?> GuidScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }
}
