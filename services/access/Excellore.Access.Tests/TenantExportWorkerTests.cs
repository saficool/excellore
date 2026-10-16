using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Excellore.Access.Worker;
using Excellore.Contracts;
using Excellore.Platform;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Access.Tests;

public sealed class TenantExportWorkerTests
{
    [Fact]
    public async Task Tenant_export_is_isolated_idempotent_and_audited()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var postgres = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(password).Build();
        await postgres.StartAsync();
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        foreach (var databaseRole in new[] { "access_migrator", "access_app", "access_publisher", "access_worker" })
            await ExecuteAsync(admin,
                $"CREATE ROLE {databaseRole} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE,CREATE ON SCHEMA public TO access_migrator");
        await ExecuteAsync(admin,
            "GRANT USAGE ON SCHEMA public TO access_app,access_publisher,access_worker");
        await using var migrator = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString())
        { Username = "access_migrator", Password = password }.ConnectionString);
        await migrator.OpenAsync();
        foreach (var migration in new[]
        {
            "001_initial.sql", "003_membership_index.sql", "004_outbox_publisher.sql",
            "005_audit_append_only.sql", "010_ownership_transfers.sql",
            "011_support_grants_rls.sql", "012_audit_export_requests.sql",
            "013_worker_expiry.sql", "015_role_assignment_batches.sql",
            "016_tenant_deletion_dispatch.sql", "017_tenant_erasure_receipts.sql",
            "018_tenant_erasure_finalization.sql", "019_tenant_export_requests.sql",
            "020_tenant_export_worker.sql"
        })
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, migration)));

        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var role = Guid.NewGuid();
        var otherRole = Guid.NewGuid();
        var export = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO tenants(id,slug,name,status) VALUES
              (@tenant,'exported-tenant','Exported tenant','active'),
              (@otherTenant,'private-tenant','Private tenant','active');
            INSERT INTO apps(id,name,core) VALUES ('access','Access',true);
            INSERT INTO identities(user_id,email) VALUES
              (@user,'exported@example.invalid'),(@otherUser,'private@example.invalid');
            INSERT INTO user_tenant_index(user_id,tenant_id,membership_status) VALUES
              (@user,@tenant,'active'),(@otherUser,@otherTenant,'active');
            INSERT INTO tenant_members(tenant_id,user_id,status) VALUES
              (@tenant,@user,'active'),(@otherTenant,@otherUser,'active');
            INSERT INTO roles(id,tenant_id,name,is_system) VALUES
              (@role,@tenant,'Exported role',false),(@otherRole,@otherTenant,'Private role',false);
            INSERT INTO role_assignments(id,tenant_id,user_id,role_id,granted_by) VALUES
              (gen_random_uuid(),@tenant,@user,@role,@user),
              (gen_random_uuid(),@otherTenant,@otherUser,@otherRole,@otherUser);
            INSERT INTO tenant_apps(tenant_id,app_id,status) VALUES
              (@tenant,'access','enabled'),(@otherTenant,'access','enabled');
            INSERT INTO tenant_settings(tenant_id,settings) VALUES
              (@tenant,'{"label":"exported-setting"}'),
              (@otherTenant,'{"label":"private-setting"}');
            INSERT INTO audit_events(id,tenant_id,actor_type,action,hash,hash_version) VALUES
              (gen_random_uuid(),@tenant,'system','exported-audit',decode('01','hex'),2),
              (gen_random_uuid(),@otherTenant,'system','private-audit',decode('02','hex'),2);
            INSERT INTO tenant_exports(id,tenant_id,requested_by) VALUES (@export,@tenant,@user);
            """, admin))
        {
            seed.Parameters.AddWithValue("tenant", tenant);
            seed.Parameters.AddWithValue("otherTenant", otherTenant);
            seed.Parameters.AddWithValue("user", user);
            seed.Parameters.AddWithValue("otherUser", otherUser);
            seed.Parameters.AddWithValue("role", role);
            seed.Parameters.AddWithValue("otherRole", otherRole);
            seed.Parameters.AddWithValue("export", export);
            await seed.ExecuteNonQueryAsync();
        }

        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString())
        { Username = "access_worker", Password = password }.ConnectionString);
        var objects = new MemoryExportStore();
        var actor = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Worker:ActorId"] = actor.ToString("D") }).Build();
        var coordinator = new TenantExportCoordinator(new AccessWorkerDatabase(source),
            new PostgresEventInbox(), objects, configuration);
        var request = Event(TenantExportRequestedV1.EventType,
            TenantExportRequestedV1.EventSource, tenant, export, user, "human");
        Assert.True(await coordinator.BuildAccessAsync(request));
        Assert.False(await coordinator.BuildAccessAsync(request));
        var access = Encoding.UTF8.GetString((await objects.GetAsync(export, "access", default))!);
        Assert.Contains("Exported tenant", access, StringComparison.Ordinal);
        Assert.Contains("exported-setting", access, StringComparison.Ordinal);
        Assert.DoesNotContain("Private tenant", access, StringComparison.Ordinal);
        Assert.DoesNotContain("private-setting", access, StringComparison.Ordinal);

        await objects.PutAsync(export, "profile", JsonSerializer.SerializeToUtf8Bytes(new
        {
            memberProfiles = new[] { new { userId = user, jobTitle = "Exported profile" } }
        }), default);
        var ready = Event(ProfileTenantExportReadyV1.EventType,
            ProfileTenantExportReadyV1.EventSource, tenant, export, actor, "machine");
        Assert.True(await coordinator.CompleteAsync(ready));
        Assert.False(await coordinator.CompleteAsync(ready));
        var complete = JsonDocument.Parse((await objects.GetAsync(export, "complete", default))!);
        Assert.Equal(tenant, complete.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal("Exported profile", complete.RootElement.GetProperty("profile")
            .GetProperty("memberProfiles")[0].GetProperty("jobTitle").GetString());
        Assert.Equal("ready", await ScalarAsync(admin,
            "SELECT status FROM tenant_exports WHERE id=@id", export));
        Assert.Equal(1L, await CountAsync(admin, """
            SELECT count(*) FROM audit_events WHERE tenant_id=@tenant
              AND action='access.tenant.export-ready' AND actor_sub=@actor
            """, tenant, actor));
        Assert.Equal(2L, await CountAsync(admin, "SELECT count(*) FROM inbox"));
    }

    private static byte[] Event(string type, string source, Guid tenant, Guid export,
        Guid actor, string actorType) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0",
            id = Guid.NewGuid(),
            type,
            source,
            time = DateTimeOffset.UtcNow,
            tenantid = tenant,
            correlationid = Guid.NewGuid(),
            actor = new { sub = actor.ToString("D"), type = actorType },
            data = new { tenantId = tenant, exportId = export }
        });

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql,
        Guid tenant = default, Guid actor = default)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (tenant != default) command.Parameters.AddWithValue("tenant", tenant);
        if (actor != default) command.Parameters.AddWithValue("actor", actor);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class MemoryExportStore : IPrivateExportStore
    {
        private readonly Dictionary<(Guid, string), byte[]> values = [];
        public Task PutAsync(Guid id, string part, byte[] data, CancellationToken ct)
        {
            values[(id, part)] = data;
            return Task.CompletedTask;
        }
        public Task<byte[]?> GetAsync(Guid id, string part, CancellationToken ct) =>
            Task.FromResult(values.GetValueOrDefault((id, part)));
        public Task DeleteAsync(Guid id, string part, CancellationToken ct)
        {
            values.Remove((id, part));
            return Task.CompletedTask;
        }
    }
}
