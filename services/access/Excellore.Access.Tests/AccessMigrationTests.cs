using System.Security.Cryptography;
using Excellore.Platform.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Access.Tests;

public sealed class AccessMigrationTests
{
    [Fact]
    public async Task InitialMigrationCreatesClassifiedForcedRlsSchemaWithRestrictedRoles()
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
        await ExecuteAsync(admin, "ALTER DEFAULT PRIVILEGES FOR ROLE access_migrator IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO access_app;");

        var migratorConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_migrator", Password = migratorPassword
        }.ConnectionString;
        await using var migrator = new NpgsqlConnection(migratorConnectionString);
        await migrator.OpenAsync();
        await ExecuteAsync(migrator, await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "001_initial.sql")));

        var appConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_app", Password = appPassword
        }.ConnectionString;
        await using var app = new NpgsqlConnection(appConnectionString);
        await app.OpenAsync();

        Assert.True((await RlsDatabaseChecks.GetRuntimeRoleStatusAsync(app)).IsSafe);
        var exemptions = RlsDatabaseChecks.ParseExemptions(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "rls-exempt.txt")));
        Assert.Equal(["outbox", "user_tenant_index", "support_grants"], exemptions);
        Assert.Empty(await RlsDatabaseChecks.FindUnprotectedTenantTablesAsync(app, exemptions));

        var classes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenants"] = "P", ["apps"] = "P", ["permissions"] = "P", ["identities"] = "P",
            ["user_tenant_index"] = "P", ["support_grants"] = "P", ["outbox"] = "P", ["inbox"] = "P",
            ["roles"] = "H", ["role_permissions"] = "H", ["audit_events"] = "H",
            ["tenant_members"] = "T", ["role_assignments"] = "T", ["groups"] = "T",
            ["group_members"] = "T", ["group_role_assignments"] = "T", ["tenant_apps"] = "T",
            ["tenant_settings"] = "T", ["invitations"] = "T", ["access_requests"] = "T"
        };
        await using (var command = new NpgsqlCommand("""
            SELECT c.relname, obj_description(c.oid), c.relrowsecurity, c.relforcerowsecurity
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r' ORDER BY c.relname
            """, app))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                var table = reader.GetString(0);
                Assert.True(classes.TryGetValue(table, out var tableClass), $"Unexpected table {table}");
                Assert.StartsWith($"Class {tableClass}", reader.GetString(1));
                Assert.Equal(tableClass != "P", reader.GetBoolean(2));
                Assert.Equal(tableClass != "P", reader.GetBoolean(3));
                seen.Add(table);
            }
            Assert.Equal(classes.Keys.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
        }

        Assert.False(await HasTablePrivilegeAsync(app, "outbox", "SELECT"));
        Assert.True(await HasTablePrivilegeAsync(app, "outbox", "INSERT"));
        Assert.False(await HasTablePrivilegeAsync(app, "audit_events", "UPDATE"));
        Assert.False(await HasTablePrivilegeAsync(app, "audit_events", "DELETE"));
        Assert.False(await HasTablePrivilegeAsync(app, "support_grants", "INSERT"));

        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();
        var ids = await SeedTenantRowsAsync(admin, tenantOne, tenantTwo);
        await VerifyTenantRowsAsync(app, tenantOne, tenantTwo);
        await VerifyTenantWriteBoundariesAsync(app, tenantOne, tenantTwo, ids);

        await ExecuteAsync(migrator, "CREATE TABLE default_grant_probe (id integer);");
        Assert.False(await HasTablePrivilegeAsync(app, "default_grant_probe", "SELECT"));
    }

    private static async Task<Dictionary<string, Guid>> SeedTenantRowsAsync(
        NpgsqlConnection admin, Guid tenantOne, Guid tenantTwo)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO tenants (id, slug, name, status) VALUES
              (@t1, 'tenant-one', 'Tenant One', 'active'),
              (@t2, 'tenant-two', 'Tenant Two', 'active');
            INSERT INTO apps (id, name, core) VALUES
              ('access', 'Access', true), ('profile', 'Profile', true);
            INSERT INTO permissions (key, app_id, description) VALUES
              ('access.app.access', 'access', 'Open Access');
            INSERT INTO identities (user_id, email) VALUES
              (@u1, 'fixture-one@example.invalid'), (@u2, 'fixture-two@example.invalid');
            INSERT INTO user_tenant_index (user_id, tenant_id, membership_status) VALUES
              (@u1, @t1, 'active'), (@u2, @t2, 'active');
            INSERT INTO roles (id, tenant_id, name, is_system) VALUES
              (@system_role, NULL, 'member', true),
              (@r1, @t1, 'tenant-role', false), (@r2, @t2, 'tenant-role', false);
            INSERT INTO role_permissions (role_id, tenant_id, permission_key) VALUES
              (@system_role, NULL, 'access.app.access'),
              (@r1, @t1, 'access.app.access'), (@r2, @t2, 'access.app.access');
            INSERT INTO tenant_members (tenant_id, user_id, status) VALUES
              (@t1, @u1, 'active'), (@t2, @u2, 'active');
            INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES
              (@a1, @t1, @u1, @r1, @u1), (@a2, @t2, @u2, @r2, @u2);
            INSERT INTO groups (id, tenant_id, name) VALUES
              (@g1, @t1, 'fixture-group'), (@g2, @t2, 'fixture-group');
            INSERT INTO group_members (tenant_id, group_id, user_id) VALUES
              (@t1, @g1, @u1), (@t2, @g2, @u2);
            INSERT INTO group_role_assignments (id, tenant_id, group_id, role_id, granted_by) VALUES
              (@ga1, @t1, @g1, @r1, @u1), (@ga2, @t2, @g2, @r2, @u2);
            INSERT INTO tenant_apps (tenant_id, app_id, status) VALUES
              (@t1, 'access', 'enabled'), (@t2, 'access', 'enabled');
            INSERT INTO tenant_settings (tenant_id) VALUES (@t1), (@t2);
            INSERT INTO invitations (id, tenant_id, email, token_hash, status, invited_by, expires_at) VALUES
              (@i1, @t1, 'invite-one@example.invalid', decode('01','hex'), 'pending', @u1, now() + interval '1 day'),
              (@i2, @t2, 'invite-two@example.invalid', decode('02','hex'), 'pending', @u2, now() + interval '1 day');
            INSERT INTO access_requests (id, tenant_id, requester_id, target_type, target_key, reason, status) VALUES
              (@q1, @t1, @u1, 'role', 'member', 'fixture', 'pending'),
              (@q2, @t2, @u2, 'role', 'member', 'fixture', 'pending');
            INSERT INTO audit_events (id, tenant_id, actor_type, action, hash) VALUES
              (@e0, NULL, 'system', 'fixture', decode('00','hex')),
              (@e1, @t1, 'system', 'fixture', decode('01','hex')),
              (@e2, @t2, 'system', 'fixture', decode('02','hex'));
            """, admin);
        command.Parameters.AddWithValue("t1", tenantOne);
        command.Parameters.AddWithValue("t2", tenantTwo);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var name in new[] { "u1", "u2", "system_role", "r1", "r2", "a1", "a2", "g1", "g2", "ga1", "ga2", "i1", "i2", "q1", "q2", "e0", "e1", "e2" })
        {
            ids[name] = Guid.NewGuid();
            command.Parameters.AddWithValue(name, ids[name]);
        }
        await command.ExecuteNonQueryAsync();
        return ids;
    }

    private static async Task VerifyTenantRowsAsync(NpgsqlConnection app, Guid tenantOne, Guid tenantTwo)
    {
        string[] tenantTables =
        [
            "tenant_members", "role_assignments", "groups", "group_members",
            "group_role_assignments", "tenant_apps", "tenant_settings", "invitations",
            "access_requests"
        ];
        foreach (var table in tenantTables) Assert.Equal(0L, await CountAsync(app, table));
        Assert.Equal(1L, await CountAsync(app, "roles"));
        Assert.Equal(1L, await CountAsync(app, "role_permissions"));
        Assert.Equal(0L, await CountAsync(app, "audit_events"));

        foreach (var (current, other) in new[] { (tenantOne, tenantTwo), (tenantTwo, tenantOne) })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true)", app))
            {
                setTenant.Parameters.AddWithValue("tenant", current.ToString("D"));
                await setTenant.ExecuteNonQueryAsync();
            }
            foreach (var table in tenantTables)
            {
                Assert.Equal(1L, await CountAsync(app, table));
                await using var update = new NpgsqlCommand($"UPDATE {table} SET tenant_id = tenant_id WHERE tenant_id = @other", app);
                update.Parameters.AddWithValue("other", other);
                Assert.Equal(0, await update.ExecuteNonQueryAsync());
            }
            Assert.Equal(2L, await CountAsync(app, "roles"));
            Assert.Equal(2L, await CountAsync(app, "role_permissions"));
            Assert.Equal(1L, await CountAsync(app, "audit_events"));
            await transaction.CommitAsync();
        }
        foreach (var table in tenantTables) Assert.Equal(0L, await CountAsync(app, table));
        Assert.Equal(1L, await CountAsync(app, "roles"));
        Assert.Equal(1L, await CountAsync(app, "role_permissions"));
        Assert.Equal(0L, await CountAsync(app, "audit_events"));
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected count."));
    }

    private static async Task VerifyTenantWriteBoundariesAsync(NpgsqlConnection app,
        Guid tenantOne, Guid tenantTwo, Dictionary<string, Guid> ids)
    {
        // Every class T table must reject a valid foreign tenant key at the RLS boundary.
        // Some rows intentionally duplicate existing keys: RLS must reject them before
        // uniqueness or foreign-key checks can expose another tenant's data.
        var foreignTenantInserts = new[]
        {
            "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
            "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @user)",
            "INSERT INTO groups (id, tenant_id, name) VALUES (@id, @tenant, 'foreign-group')",
            "INSERT INTO group_members (tenant_id, group_id, user_id) VALUES (@tenant, @group, @user)",
            "INSERT INTO group_role_assignments (id, tenant_id, group_id, role_id, granted_by) VALUES (@id, @tenant, @group, @role, @user)",
            "INSERT INTO tenant_apps (tenant_id, app_id, status) VALUES (@tenant, 'profile', 'enabled')",
            "INSERT INTO tenant_settings (tenant_id) VALUES (@tenant)",
            "INSERT INTO invitations (id, tenant_id, email, token_hash, status, invited_by, expires_at) VALUES (@id, @tenant, 'foreign@example.invalid', decode('03','hex'), 'pending', @user, now() + interval '1 day')",
            "INSERT INTO access_requests (id, tenant_id, requester_id, target_type, target_key, reason, status) VALUES (@id, @tenant, @user, 'role', 'member', 'fixture', 'pending')"
        };
        foreach (var insert in foreignTenantInserts)
        {
            await AssertRejectedAsync(app, tenantOne, PostgresErrorCodes.InsufficientPrivilege,
                insert, ("id", Guid.NewGuid()), ("tenant", tenantTwo),
                ("user", ids["u2"]), ("role", ids["system_role"]), ("group", ids["g2"]));
        }

        await AssertRejectedAsync(app, tenantOne, PostgresErrorCodes.InsufficientPrivilege,
            "INSERT INTO tenant_apps (tenant_id, app_id, status) VALUES (@tenant, 'profile', 'enabled')",
            ("tenant", tenantTwo));
        await AssertRejectedAsync(app, tenantOne, PostgresErrorCodes.CheckViolation,
            "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @user)",
            ("id", Guid.NewGuid()), ("tenant", tenantOne), ("user", ids["u1"]), ("role", ids["r2"]));
        await AssertRejectedAsync(app, tenantOne, PostgresErrorCodes.CheckViolation,
            "INSERT INTO role_permissions (role_id, tenant_id, permission_key) VALUES (@role, @tenant, 'access.app.access')",
            ("role", ids["r2"]), ("tenant", tenantOne));
        await AssertRejectedAsync(app, tenantOne, PostgresErrorCodes.ForeignKeyViolation,
            "INSERT INTO group_members (tenant_id, group_id, user_id) VALUES (@tenant, @group, @user)",
            ("tenant", tenantOne), ("group", ids["g2"]), ("user", ids["u1"]));
        await AssertRejectedAsync(app, tenantOne, PostgresErrorCodes.ForeignKeyViolation,
            "INSERT INTO group_role_assignments (id, tenant_id, group_id, role_id, granted_by) VALUES (@id, @tenant, @group, @role, @user)",
            ("id", Guid.NewGuid()), ("tenant", tenantOne), ("group", ids["g2"]),
            ("role", ids["r1"]), ("user", ids["u1"]));
        await AssertRejectedAsync(app, tenantOne, PostgresErrorCodes.InsufficientPrivilege,
            "INSERT INTO roles (id, tenant_id, name, is_system) VALUES (@id, NULL, 'unauthorized-system', true)",
            ("id", Guid.NewGuid()));
    }

    private static async Task AssertRejectedAsync(NpgsqlConnection app, Guid tenantId,
        string expectedSqlState, string sql, params (string Name, object Value)[] parameters)
    {
        await using var transaction = await app.BeginTransactionAsync();
        await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true)", app))
        {
            setTenant.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await setTenant.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, app);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(expectedSqlState, error.SqlState);
        await transaction.RollbackAsync();
    }

    private static async Task<bool> HasTablePrivilegeAsync(NpgsqlConnection connection, string table, string privilege)
    {
        await using var command = new NpgsqlCommand("SELECT has_table_privilege(current_user, @table, @privilege)", connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
