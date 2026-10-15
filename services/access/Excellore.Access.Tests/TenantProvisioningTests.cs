using System.Security.Cryptography;
using Excellore.Access.Application;
using Excellore.Access.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Access.Tests;

public sealed class TenantProvisioningTests
{
    [Fact]
    public async Task ProvisioningIdentityAndMembershipIndexKeepTenantBoundaries()
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
            "005_audit_append_only.sql", "006_custom_role_lifecycle.sql" })
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, migration)));

        var appString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_app", Password = appPassword
        }.ConnectionString;
        await using var dataSource = NpgsqlDataSource.Create(appString);
        var actor = Guid.NewGuid();
        var catalog = new PermissionCatalog("access", [
            new CatalogPermission("access.app.access", "Open Access", false, false, false,
                ["owner", "admin", "member", "viewer", "auditor"])
        ]);
        Assert.Equal(CatalogSyncResult.Updated,
            await new CatalogSyncService(dataSource).SyncAsync("access", catalog, actor));

        var mirror = new IdentityMirrorService(dataSource);
        var user = Guid.NewGuid();
        var firstIdentity = new VerifiedIdentity(user, "user@example.invalid", true, "First", null);
        var concurrent = await Task.WhenAll(mirror.UpsertAsync(firstIdentity), mirror.UpsertAsync(firstIdentity));
        Assert.Contains(IdentityMirrorResult.Created, concurrent);
        Assert.Contains(IdentityMirrorResult.Unchanged, concurrent);
        Assert.Equal(IdentityMirrorResult.Updated,
            await mirror.UpsertAsync(new VerifiedIdentity(user, "user@example.invalid", true, "Changed", null)));
        Assert.Equal(1L, await CountAsync(admin, "outbox WHERE type = 'identity.user.created.v1'"));
        Assert.Equal(1L, await CountAsync(admin, "outbox WHERE type = 'identity.user.updated.v1'"));
        Assert.Equal(0L, await CountAsync(admin,
            "outbox WHERE type LIKE 'identity.%' AND payload::text LIKE '%example.invalid%'"));

        var service = new TenantProvisioningService(dataSource);
        Assert.Equal(CreateTenantStatus.Invalid,
            (await service.CreateAsync(new("Name", "admin", "starter", "owner@example.invalid", []), actor)).Status);
        Assert.Equal(CreateTenantStatus.Invalid,
            (await service.CreateAsync(new("Name", "tenant-one", "starter", "owner@example.invalid", ["unknown"]), actor)).Status);
        var one = await service.CreateAsync(new("Tenant One", "tenant-one", "starter", "owner@example.invalid", []), actor);
        var two = await service.CreateAsync(new("Tenant Two", "tenant-two", "starter", "owner@example.invalid", ["profile"]), actor);
        Assert.Equal(CreateTenantStatus.Created, one.Status);
        Assert.Equal(CreateTenantStatus.Created, two.Status);
        Assert.Equal(CreateTenantStatus.SlugTaken,
            (await service.CreateAsync(new("Another", "tenant-one", "starter", "owner@example.invalid", []), actor)).Status);
        var tenantOne = one.Tenant!.TenantId;
        var tenantTwo = two.Tenant!.TenantId;
        Assert.Equal("provisioning", one.Tenant.Status);
        Assert.Equal(2L, await CountAsync(admin, "tenants WHERE status = 'provisioning'"));
        Assert.Equal(0L, await CountAsync(admin, "tenant_members"));
        Assert.Equal(0L, await CountAsync(admin, "outbox WHERE type = 'tenant.created.v1'"));
        Assert.Equal(2L, await CountAsync(admin, "audit_events WHERE action = 'access.tenant.provisioned'"));
        Assert.Equal(4L, await CountAsync(admin, "tenant_apps WHERE status = 'enabled'"));
        Assert.Equal(2L, await CountAsync(admin, "tenant_settings"));
        Assert.Equal(2L, await CountAsync(admin, "invitations WHERE status = 'pending'"));
        Assert.Equal(1L, await CountAsync(admin,
            $"invitations WHERE id = '{one.Tenant.OwnerInvitationId:D}' AND octet_length(token_hash) = 32 AND expires_at > now()"));
        Assert.Equal(0L, await CountAsync(admin,
            "audit_events WHERE action = 'access.tenant.provisioned' AND (target_id LIKE '%example%' OR reason LIKE '%example%')"));

        await using var app = new NpgsqlConnection(appString);
        await app.OpenAsync();
        Assert.Equal(0L, await CountAsync(app, "tenant_apps"));
        Assert.Equal(0L, await CountAsync(app, "invitations"));
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, transaction, tenantOne);
            Assert.Equal(2L, await CountAsync(app, "tenant_apps"));
            Assert.Equal(1L, await CountAsync(app, "invitations"));
            await InsertMemberAsync(app, transaction, tenantOne, user, "active");
            Assert.Equal(1L, await CountAsync(app, $"user_tenant_index WHERE tenant_id = '{tenantOne:D}'"));
            await transaction.RollbackAsync();
        }
        Assert.Equal(0L, await CountAsync(admin, "user_tenant_index"));
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, transaction, tenantOne);
            await InsertMemberAsync(app, transaction, tenantOne, user, "active");
            await transaction.CommitAsync();
        }
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, transaction, tenantTwo);
            Assert.Equal(0L, await CountAsync(app, "tenant_members"));
            await InsertMemberAsync(app, transaction, tenantTwo, user, "suspended");
            await transaction.CommitAsync();
        }
        Assert.Equal(2L, await CountAsync(admin, "user_tenant_index"));
        Assert.Equal(1L, await CountAsync(admin,
            $"user_tenant_index WHERE tenant_id = '{tenantTwo:D}' AND membership_status = 'suspended'"));
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, transaction, tenantOne);
            await ExecuteAsync(app, "UPDATE tenant_members SET status = 'removed' WHERE user_id = @user",
                transaction, ("user", user));
            await transaction.CommitAsync();
        }
        Assert.Equal(1L, await CountAsync(admin,
            $"user_tenant_index WHERE tenant_id = '{tenantOne:D}' AND membership_status = 'removed'"));
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, transaction, tenantOne);
            await ExecuteAsync(app, "DELETE FROM tenant_members WHERE user_id = @user", transaction, ("user", user));
            await transaction.CommitAsync();
        }
        Assert.Equal(0L, await CountAsync(admin,
            $"user_tenant_index WHERE tenant_id = '{tenantOne:D}'"));
        Assert.Equal(1L, await CountAsync(admin,
            $"user_tenant_index WHERE tenant_id = '{tenantTwo:D}'"));
    }

    private static async Task InsertMemberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenant, Guid user, string status) => await ExecuteAsync(connection,
        "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, @status)",
        transaction, ("tenant", tenant), ("user", user), ("status", status));

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant) =>
        await ExecuteAsync(connection, "SELECT set_config('app.tenant_id', @tenant, true)",
            transaction, ("tenant", tenant.ToString("D")));

    private static async Task<long> CountAsync(NpgsqlConnection connection, string where)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {where}", connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql,
        NpgsqlTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
