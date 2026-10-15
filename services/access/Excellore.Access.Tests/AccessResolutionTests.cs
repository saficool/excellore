using System.Security.Cryptography;
using System.Diagnostics;
using Excellore.Access.Application;
using Excellore.Access.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Access.Tests;

[CollectionDefinition("Access resolution latency", DisableParallelization = true)]
public sealed class AccessResolutionLatencyCollection;

[Collection("Access resolution latency")]
public sealed class AccessResolutionTests
{
    [Fact]
    public async Task Internal_resolution_rechecks_tenant_membership_and_enabled_apps()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"CREATE ROLE access_migrator LOGIN PASSWORD '{migratorPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, $"CREATE ROLE access_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE, CREATE ON SCHEMA public TO access_migrator");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO access_app");
        var migratorString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_migrator", Password = migratorPassword
        }.ConnectionString;
        await using var migrator = new NpgsqlConnection(migratorString);
        await migrator.OpenAsync();
        foreach (var migration in new[] { "001_initial.sql", "002_register_apps.sql", "003_membership_index.sql",
            "005_audit_append_only.sql", "006_custom_role_lifecycle.sql",
            "011_support_grants_rls.sql" })
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, migration)));
        await ExecuteAsync(migrator, "INSERT INTO apps (id, name, core) VALUES ('reports', 'Reports', false)");

        var appString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_app", Password = appPassword
        }.ConnectionString;
        await using var dataSource = NpgsqlDataSource.Create(appString);
        var actor = Guid.NewGuid();
        var catalog = new CatalogSyncService(dataSource);
        Assert.Equal(CatalogSyncResult.Updated, await catalog.SyncAsync("access", new("access", [
            new("access.app.access", "Open Access", false, false, false, ["owner", "viewer"]),
            new("access.member.read", "Read members", false, false, false, ["owner", "viewer"])
        ]), actor));
        Assert.Equal(CatalogSyncResult.Updated, await catalog.SyncAsync("reports", new("reports", [
            new("reports.app.access", "Open Reports", false, false, false, ["owner"]),
            new("reports.data.read", "Read reports", false, false, false, ["owner"])
        ]), actor));

        var person = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var mirror = new IdentityMirrorService(dataSource);
        await mirror.UpsertAsync(new VerifiedIdentity(person, "person@example.invalid", true, null, null));
        await mirror.UpsertAsync(new VerifiedIdentity(stranger, "stranger@example.invalid", true, null, null));
        var provisioner = new TenantProvisioningService(dataSource);
        var first = (await provisioner.CreateAsync(new("First", "first-tenant", "starter",
            "owner@example.invalid", ["reports"]), actor)).Tenant!;
        var second = (await provisioner.CreateAsync(new("Second", "second-tenant", "starter",
            "owner@example.invalid", []), actor)).Tenant!;
        await ExecuteAsync(admin, "UPDATE tenants SET status = 'active' WHERE id IN (@one, @two)",
            null, ("one", first.TenantId), ("two", second.TenantId));
        var ownerRole = (Guid)(await ScalarAsync(admin,
            "SELECT id FROM roles WHERE tenant_id IS NULL AND name = 'owner'"))!;
        var viewerRole = (Guid)(await ScalarAsync(admin,
            "SELECT id FROM roles WHERE tenant_id IS NULL AND name = 'viewer'"))!;

        await using var app = new NpgsqlConnection(appString);
        await app.OpenAsync();
        await using (var tx = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, tx, first.TenantId);
            await ExecuteAsync(app, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
                tx, ("tenant", first.TenantId), ("user", person));
            await ExecuteAsync(app, """
                INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by)
                VALUES (@id, @tenant, @user, @role, @actor)
                """, tx, ("id", Guid.NewGuid()), ("tenant", first.TenantId), ("user", person),
                ("role", ownerRole), ("actor", actor));
            await tx.CommitAsync();
        }
        await using (var tx = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, tx, second.TenantId);
            await ExecuteAsync(app, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
                tx, ("tenant", second.TenantId), ("user", stranger));
            await tx.CommitAsync();
        }

        var service = new AccessResolutionService(dataSource);
        var allowed = await service.ResolveAsync(person, first.TenantId);
        Assert.True(allowed.Allowed);
        Assert.Equal(["owner"], allowed.Roles);
        Assert.Equal(["access.app.access", "access.member.read", "reports.app.access", "reports.data.read"], allowed.Permissions);
        Assert.Equal(["access", "reports", "shell"], allowed.Apps);
        Assert.True(allowed.Version >= 1);
        for (var warmup = 0; warmup < 10; warmup++)
            Assert.True((await service.ResolveAsync(person, first.TenantId)).Allowed);
        var samples = new List<double>();
        for (var sample = 0; sample < 60; sample++)
        {
            var timer = Stopwatch.StartNew();
            Assert.True((await service.ResolveAsync(person, first.TenantId)).Allowed);
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
        Assert.True(p95 < 50, $"Local Access resolve p95 was {p95:F1} ms; target is below 50 ms.");
        var denied = await service.ResolveAsync(person, second.TenantId);
        Assert.False(denied.Allowed);
        Assert.Empty(denied.Permissions);
        Assert.Empty(denied.Apps);
        Assert.False((await service.ResolveAsync(Guid.NewGuid(), first.TenantId)).Allowed);
        Assert.False((await service.ResolveAsync(person, Guid.NewGuid())).Allowed);
        // Even a stale or tampered class P lookup row cannot authorize a tenant.
        await ExecuteAsync(app, """
            INSERT INTO user_tenant_index (user_id, tenant_id, membership_status)
            VALUES (@user, @tenant, 'active')
            """, null, ("user", person), ("tenant", second.TenantId));
        var listed = await service.ListTenantsAsync(person);
        Assert.Single(listed);
        Assert.Equal(first.TenantId, listed[0].TenantId);
        Assert.Equal("first-tenant", (await service.ResolveSlugAsync(person, "first-tenant"))?.Slug);
        Assert.Null(await service.ResolveSlugAsync(person, "second-tenant"));
        Assert.Null(await service.ResolveSlugAsync(stranger, "first-tenant"));

        await using (var tx = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, tx, first.TenantId);
            await ExecuteAsync(app,
                "UPDATE tenant_apps SET status = 'disabled' WHERE tenant_id = @tenant AND app_id = 'reports'",
                tx, ("tenant", first.TenantId));
            await tx.CommitAsync();
        }
        var disabled = await service.ResolveAsync(person, first.TenantId);
        Assert.Equal(["access.app.access", "access.member.read"], disabled.Permissions);
        Assert.Equal(["access", "shell"], disabled.Apps);

        await using (var tx = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, tx, first.TenantId);
            await ExecuteAsync(app, "UPDATE role_assignments SET expires_at = now() - interval '1 second' WHERE user_id = @user",
                tx, ("user", person));
            await tx.CommitAsync();
        }
        var expired = await service.ResolveAsync(person, first.TenantId);
        Assert.True(expired.Allowed);
        Assert.Empty(expired.Roles);
        Assert.Empty(expired.Permissions);
        Assert.Equal(["shell"], expired.Apps);

        var group = Guid.NewGuid();
        await using (var tx = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, tx, first.TenantId);
            await ExecuteAsync(app,
                "INSERT INTO groups (id, tenant_id, name) VALUES (@group, @tenant, 'reviewers')",
                tx, ("group", group), ("tenant", first.TenantId));
            await ExecuteAsync(app,
                "INSERT INTO group_members (tenant_id, group_id, user_id) VALUES (@tenant, @group, @user)",
                tx, ("tenant", first.TenantId), ("group", group), ("user", person));
            await ExecuteAsync(app, """
                INSERT INTO group_role_assignments (id, tenant_id, group_id, role_id, granted_by)
                VALUES (@id, @tenant, @group, @role, @actor)
                """, tx, ("id", Guid.NewGuid()), ("tenant", first.TenantId), ("group", group),
                ("role", ownerRole), ("actor", actor));
            await tx.CommitAsync();
        }
        var viaGroup = await service.ResolveAsync(person, first.TenantId);
        Assert.Equal(["owner"], viaGroup.Roles);
        Assert.Contains("access.app.access", viaGroup.Permissions);

        var staff = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        await mirror.UpsertAsync(new VerifiedIdentity(staff, "staff@example.invalid", true, null, null));
        await ExecuteAsync(admin, """
            INSERT INTO support_grants
              (id, tenant_id, staff_user_id, role_id, reason, status, expires_at)
            VALUES (@grant, @tenant, @staff, @role, 'support-review', 'requested', now() + interval '1 hour')
            """, null, ("grant", grantId), ("tenant", first.TenantId),
            ("staff", staff), ("role", viewerRole));
        await ExecuteAsync(admin, """
            INSERT INTO tenant_members
              (tenant_id, user_id, status, is_support, support_grant_id, expires_at)
            VALUES (@tenant, @staff, 'active', true, @grant, now() + interval '1 hour')
            """, null, ("tenant", first.TenantId), ("staff", staff), ("grant", grantId));
        Assert.False((await service.ResolveAsync(staff, first.TenantId)).Allowed);
        await ExecuteAsync(admin, "UPDATE support_grants SET status = 'active' WHERE id = @grant",
            null, ("grant", grantId));
        await ExecuteAsync(admin, """
            INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by)
            VALUES (@id, @tenant, @staff, @viewer, @actor),
                   (@other, @tenant, @staff, @owner, @actor)
            """, null, ("id", Guid.NewGuid()), ("other", Guid.NewGuid()),
            ("tenant", first.TenantId), ("staff", staff), ("viewer", viewerRole),
            ("owner", ownerRole), ("actor", actor));
        var supportAccess = await service.ResolveAsync(staff, first.TenantId);
        Assert.True(supportAccess.Allowed);
        Assert.Equal(["viewer"], supportAccess.Roles);
        Assert.Equal(["access.app.access", "access.member.read"], supportAccess.Permissions);
        Assert.Equal(["access", "shell"], supportAccess.Apps);
        await ExecuteAsync(admin,
            "UPDATE support_grants SET expires_at = now() - interval '1 second' WHERE id = @grant",
            null, ("grant", grantId));
        Assert.False((await service.ResolveAsync(staff, first.TenantId)).Allowed);

        await ExecuteAsync(admin, "UPDATE tenants SET status = 'suspended' WHERE id = @tenant",
            null, ("tenant", first.TenantId));
        Assert.False((await service.ResolveAsync(person, first.TenantId)).Allowed);
        Assert.Empty(await service.ListTenantsAsync(person));
        Assert.Null(await service.ResolveSlugAsync(person, "first-tenant"));
        await ExecuteAsync(admin, "UPDATE tenants SET status = 'active' WHERE id = @tenant",
            null, ("tenant", first.TenantId));
        await using (var tx = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, tx, first.TenantId);
            await ExecuteAsync(app, "UPDATE tenant_members SET status = 'removed' WHERE user_id = @user",
                tx, ("user", person));
            await tx.CommitAsync();
        }
        Assert.False((await service.ResolveAsync(person, first.TenantId)).Allowed);
        Assert.Empty(await service.ListTenantsAsync(person));
        Assert.Null(await service.ResolveSlugAsync(person, "first-tenant"));
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid tenant) =>
        await ExecuteAsync(connection, "SELECT set_config('app.tenant_id', @tenant, true)",
            tx, ("tenant", tenant.ToString("D")));

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql,
        NpgsqlTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
