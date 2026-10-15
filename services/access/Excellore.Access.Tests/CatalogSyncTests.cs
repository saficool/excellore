using System.Security.Cryptography;
using System.Text.Json;
using Excellore.Access.Application;
using Excellore.Access.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Access.Tests;

public sealed class CatalogSyncTests
{
    [Fact]
    public async Task SyncIsIdempotentAndNeverChangesCustomRoleGrants()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"CREATE ROLE access_migrator LOGIN PASSWORD '{migratorPassword}' NOSUPERUSER NOBYPASSRLS;");
        await ExecuteAsync(admin, $"CREATE ROLE access_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS;");
        await ExecuteAsync(admin, "GRANT USAGE, CREATE ON SCHEMA public TO access_migrator;");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO access_app;");
        var migratorString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_migrator", Password = migratorPassword
        }.ConnectionString;
        await using var migrator = new NpgsqlConnection(migratorString);
        await migrator.OpenAsync();
        await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "001_initial.sql")));
        await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "002_register_apps.sql")));
        await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "005_audit_append_only.sql")));
        await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "006_custom_role_lifecycle.sql")));

        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO tenants (id, slug, name, status) VALUES
              (@t1, 'catalog-one', 'Catalog One', 'active'),
              (@t2, 'catalog-two', 'Catalog Two', 'active');
            """, admin))
        {
            seed.Parameters.AddWithValue("t1", tenantOne);
            seed.Parameters.AddWithValue("t2", tenantTwo);
            await seed.ExecuteNonQueryAsync();
        }

        var appString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_app", Password = appPassword
        }.ConnectionString;
        await using var dataSource = NpgsqlDataSource.Create(appString);
        var service = new CatalogSyncService(dataSource);
        var actor = Guid.NewGuid();
        var first = Catalog(
            new CatalogPermission("access.app.access", "Open Access", false, false, false,
                ["owner", "admin", "member", "viewer", "auditor"]),
            new CatalogPermission("access.role.manage", "Manage roles", true, false, false,
                ["owner", "admin"]),
            new CatalogPermission("access.new.feature", "New feature", false, false, false, ["owner"]));
        Assert.Equal(CatalogSyncResult.Updated, await service.SyncAsync("access", first, actor));
        Assert.Equal(CatalogSyncResult.Unchanged, await service.SyncAsync("access", first, actor));
        Assert.Equal(5L, await CountAsync(admin, "roles WHERE is_system"));
        Assert.Equal(8L, await CountAsync(admin, "role_permissions"));
        Assert.Equal(1L, await CountAsync(admin,
            "role_permissions WHERE permission_key = 'access.new.feature'"));
        Assert.Equal(1L, await CountAsync(admin, "audit_events"));
        Assert.Equal(2L, await CountAsync(admin, "outbox"));
        Assert.Equal(2L, await CountAsync(admin, "tenants WHERE authz_version = 2"));
        await using (var eventCommand = new NpgsqlCommand("SELECT payload::text FROM outbox ORDER BY tenant_id", admin))
        await using (var reader = await eventCommand.ExecuteReaderAsync())
        {
            var seen = new HashSet<Guid>();
            while (await reader.ReadAsync())
            {
                using var document = JsonDocument.Parse(reader.GetString(0));
                var root = document.RootElement;
                Assert.Equal("access.authz.changed.v1", root.GetProperty("type").GetString());
                var tenant = root.GetProperty("data").GetProperty("tenantId").GetGuid();
                Assert.Equal(tenant, root.GetProperty("tenantid").GetGuid());
                Assert.Equal(2, root.GetProperty("data").GetProperty("authzVersion").GetInt64());
                Assert.True(seen.Add(tenant));
            }
            Assert.Equal(2, seen.Count);
        }

        var customRole = Guid.NewGuid();
        await using (var addCustom = new NpgsqlCommand("""
            INSERT INTO roles (id, tenant_id, name, is_system) VALUES (@role, @tenant, 'custom', false);
            INSERT INTO role_permissions (role_id, tenant_id, permission_key)
              VALUES (@role, @tenant, 'access.role.manage');
            """, admin))
        {
            addCustom.Parameters.AddWithValue("role", customRole);
            addCustom.Parameters.AddWithValue("tenant", tenantOne);
            await addCustom.ExecuteNonQueryAsync();
        }

        var deprecated = Catalog(first.Permissions![0],
            first.Permissions[1] with { Deprecated = true, SystemRoles = [] }, first.Permissions[2]);
        Assert.Equal(CatalogSyncResult.Updated, await service.SyncAsync("access", deprecated, actor));
        Assert.Equal(1L, await CountAsync(admin,
            "role_permissions WHERE permission_key = 'access.role.manage'"));
        Assert.Equal(CatalogSyncResult.Conflict, await service.SyncAsync("access",
            Catalog(first.Permissions[0], first.Permissions[2]), actor));
        Assert.Equal(2L, await CountAsync(admin, "audit_events"));
        Assert.Equal(4L, await CountAsync(admin, "outbox"));

        await using (var deleteCustom = new NpgsqlCommand(
            "DELETE FROM role_permissions WHERE role_id = @role", admin))
        {
            deleteCustom.Parameters.AddWithValue("role", customRole);
            await deleteCustom.ExecuteNonQueryAsync();
        }
        Assert.Equal(CatalogSyncResult.Conflict, await service.SyncAsync("access",
            Catalog(first.Permissions[0], first.Permissions[2]), actor));
        Assert.Equal(1L, await CountAsync(admin,
            "permissions WHERE key = 'access.role.manage'"));
        Assert.Equal(2L, await CountAsync(admin, "audit_events"));
        Assert.Equal(4L, await CountAsync(admin, "outbox"));
        Assert.Equal(2L, await CountAsync(admin, "tenants WHERE authz_version = 3"));
        await using (var auditCommand = new NpgsqlCommand("""
            SELECT tenant_id, actor_sub, reason, prev_hash, hash
            FROM audit_events ORDER BY occurred_at, id
            """, admin))
        await using (var reader = await auditCommand.ExecuteReaderAsync())
        {
            byte[]? previous = null;
            var count = 0;
            while (await reader.ReadAsync())
            {
                Assert.True(reader.IsDBNull(0));
                Assert.Equal(actor, reader.GetGuid(1));
                Assert.Equal("permission catalog deployment", reader.GetString(2));
                if (previous is null) Assert.True(reader.IsDBNull(3));
                else Assert.Equal(previous, (byte[])reader.GetValue(3));
                previous = (byte[])reader.GetValue(4);
                Assert.Equal(32, previous.Length);
                count++;
            }
            Assert.Equal(2, count);
        }

        Assert.Equal(CatalogSyncResult.Invalid, await service.SyncAsync("access",
            Catalog(first.Permissions[0], first.Permissions[0]), actor));
        Assert.Equal(CatalogSyncResult.UnknownApp, await service.SyncAsync("unknown",
            new PermissionCatalog("unknown", [new CatalogPermission(
                "unknown.app.access", "Open unknown", false, false, false, ["owner"])]), actor));
        Assert.Equal(2L, await CountAsync(admin, "audit_events"));
    }

    private static PermissionCatalog Catalog(params CatalogPermission[] permissions) =>
        new("access", permissions);

    private static async Task<long> CountAsync(NpgsqlConnection connection, string tableAndCondition)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {tableAndCondition}", connection);
        return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException());
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
