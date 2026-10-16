using System.Security.Cryptography;
using Excellore.Access.Infrastructure;
using Excellore.Access.Worker;
using Excellore.Contracts;
using Excellore.Platform;
using Npgsql;
using System.Text.Json;
using Testcontainers.PostgreSql;

namespace Excellore.Access.Tests;

public sealed class TenantDeletionDispatchTests
{
    [Fact]
    public async Task Only_due_tenant_is_dispatched_once_with_machine_audit_and_valid_event()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var postgres = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(password).Build();
        await postgres.StartAsync();
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        foreach (var role in new[] { "access_migrator", "access_app", "access_publisher", "access_worker" })
            await ExecuteAsync(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE,CREATE ON SCHEMA public TO access_migrator");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO access_app,access_publisher,access_worker");
        await using var migrator = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString()) { Username = "access_migrator", Password = password }.ConnectionString);
        await migrator.OpenAsync();
        foreach (var migration in new[]
        {
            "001_initial.sql", "003_membership_index.sql", "004_outbox_publisher.sql",
            "005_audit_append_only.sql", "010_ownership_transfers.sql",
            "011_support_grants_rls.sql", "012_audit_export_requests.sql",
            "013_worker_expiry.sql", "015_role_assignment_batches.sql",
            "016_tenant_deletion_dispatch.sql", "017_tenant_erasure_receipts.sql",
            "018_tenant_erasure_finalization.sql"
        })
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, migration)));

        var due = Guid.NewGuid();
        var future = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO tenants(id,slug,name,status,deletion_due_at)
            VALUES (@due,'due-tenant','Due','pending_deletion',now()-interval '1 minute'),
                   (@future,'future-tenant','Future','pending_deletion',now()+interval '1 day')
            """, admin))
        {
            seed.Parameters.AddWithValue("due", due);
            seed.Parameters.AddWithValue("future", future);
            await seed.ExecuteNonQueryAsync();
        }
        var exportKey = $"audit-exports/{Guid.NewGuid():D}.jsonl";
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO tenant_settings(tenant_id) VALUES (@due),(@future);
            INSERT INTO audit_exports(id,tenant_id,requested_by,status,object_key)
            VALUES (gen_random_uuid(),@due,gen_random_uuid(),'ready',@key);
            """, admin))
        {
            seed.Parameters.AddWithValue("due", due);
            seed.Parameters.AddWithValue("future", future);
            seed.Parameters.AddWithValue("key", exportKey);
            await seed.ExecuteNonQueryAsync();
        }
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString()) { Username = "access_worker", Password = password }.ConnectionString);
        var actor = Guid.NewGuid();
        Assert.False(await new TenantDeletionDispatchService(source, new FixedTenant(future))
            .DispatchAsync(actor));
        Assert.True(await new TenantDeletionDispatchService(source, new FixedTenant(due))
            .DispatchAsync(actor));
        Assert.False(await new TenantDeletionDispatchService(source, new FixedTenant(due))
            .DispatchAsync(actor));
        Assert.Equal(1L, await CountAsync(admin,
            "SELECT count(*) FROM outbox WHERE tenant_id=@tenant AND type='tenant.deleted.v1'", due));
        Assert.Equal(0L, await CountAsync(admin,
            "SELECT count(*) FROM outbox WHERE tenant_id=@tenant AND type='tenant.deleted.v1'", future));
        Assert.Equal(1L, await CountAsync(admin,
            "SELECT count(*) FROM audit_events WHERE tenant_id=@tenant AND actor_type='service' AND action='access.tenant.deletion-dispatched'", due));
        var receipt = new ProfileErasureConfirmationProjector(
            new AccessWorkerDatabase(source), new PostgresEventInbox());
        var earlyConfirmation = Confirmation(future);
        await Assert.ThrowsAsync<InvalidPlatformEventException>(() =>
            receipt.ApplyAsync(earlyConfirmation));
        var confirmation = Confirmation(due);
        Assert.True(await receipt.ApplyAsync(confirmation));
        Assert.False(await receipt.ApplyAsync(confirmation));
        Assert.Equal(1L, await CountAsync(admin,
            "SELECT count(*) FROM tenant_tombstones WHERE tenant_id=@tenant AND profile_confirmed_at IS NOT NULL", due));
        Assert.Equal(0L, await CountAsync(admin,
            "SELECT count(*) FROM tenant_tombstones WHERE tenant_id=@tenant", future));
        byte[] originalAuditHash;
        await using (var hash = new NpgsqlCommand("""
            SELECT hash FROM audit_events WHERE tenant_id=@tenant
              AND action='access.tenant.deletion-dispatched'
            """, admin))
        {
            hash.Parameters.AddWithValue("tenant", due);
            originalAuditHash = (byte[])(await hash.ExecuteScalarAsync())!;
        }
        await using (var eventCommand = new NpgsqlCommand(
            "SELECT payload::text FROM outbox WHERE tenant_id=@tenant AND type='tenant.deleted.v1'", admin))
        {
            eventCommand.Parameters.AddWithValue("tenant", due);
            var payload = (string)(await eventCommand.ExecuteScalarAsync())!;
            Assert.Equal(due, TenantDeletedV1.Parse(System.Text.Encoding.UTF8.GetBytes(payload)).Data!.TenantId);
        }
        var objects = new MemoryStore();
        var finalizer = new TenantErasureFinalizer(source, new FixedTenant(due), objects);
        Assert.True(await finalizer.FinalizeAsync(actor));
        Assert.False(await finalizer.FinalizeAsync(actor));
        Assert.Equal([exportKey], objects.Deleted);
        Assert.Equal(0L, await CountAsync(admin,
            "SELECT count(*) FROM tenants WHERE id=@tenant", due));
        Assert.Equal(0L, await CountAsync(admin,
            "SELECT count(*) FROM tenant_settings WHERE tenant_id=@tenant", due));
        Assert.Equal(1L, await CountAsync(admin,
            "SELECT count(*) FROM tenant_settings WHERE tenant_id=@tenant", future));
        Assert.Equal(0L, await CountAsync(admin,
            "SELECT count(*) FROM audit_events WHERE tenant_id=@tenant", due));
        Assert.Equal(2L, await CountAsync(migrator,
            "SELECT count(*) FROM audit_events_archive WHERE tenant_id=@tenant", due));
        await using (var hash = new NpgsqlCommand("""
            SELECT hash FROM audit_events_archive WHERE tenant_id=@tenant
              AND action='access.tenant.deletion-dispatched'
            """, migrator))
        {
            hash.Parameters.AddWithValue("tenant", due);
            Assert.Equal(originalAuditHash, (byte[])(await hash.ExecuteScalarAsync())!);
        }
        Assert.Equal(1L, await CountAsync(admin,
            "SELECT count(*) FROM tenant_tombstones WHERE tenant_id=@tenant AND completed_at IS NOT NULL", due));
        await using (var verify = await migrator.BeginTransactionAsync())
        {
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('app.tenant_id',@tenant,true)", migrator, verify))
            {
                scope.Parameters.AddWithValue("tenant", due.ToString("D"));
                await scope.ExecuteNonQueryAsync();
            }
            foreach (var table in new[]
            {
                "tenant_members", "role_assignments", "groups", "group_members",
                "group_role_assignments", "tenant_apps", "tenant_settings",
                "invitations", "access_requests", "support_grants",
                "ownership_transfers", "audit_exports", "role_assignment_batches",
                "roles", "role_permissions", "audit_events"
            })
            {
                await using var count = new NpgsqlCommand(
                    $"SELECT count(*) FROM {table} WHERE tenant_id=@tenant", migrator, verify);
                count.Parameters.AddWithValue("tenant", due);
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            }
            await verify.CommitAsync();
        }
        await using (var confirm = new NpgsqlCommand(
            "SELECT payload::text FROM outbox WHERE tenant_id=@tenant AND type='access.erasure.confirmed.v1'",
            admin))
        {
            confirm.Parameters.AddWithValue("tenant", due);
            var payload = (string)(await confirm.ExecuteScalarAsync())!;
            Assert.Equal(due, AccessErasureConfirmedV1.Parse(
                System.Text.Encoding.UTF8.GetBytes(payload)).Data!.TenantId);
        }
        await using var roleCheck = new NpgsqlCommand(
            "SELECT rolbypassrls FROM pg_roles WHERE rolname=current_user", await source.OpenConnectionAsync());
        Assert.False((bool)(await roleCheck.ExecuteScalarAsync())!);
        await using var archivePrivilege = new NpgsqlCommand(
            "SELECT has_table_privilege(current_user,'audit_events_archive','SELECT')",
            roleCheck.Connection);
        Assert.False((bool)(await archivePrivilege.ExecuteScalarAsync())!);
    }

    private sealed class FixedTenant(Guid id) : ITenantIdAccessor
    {
        public Guid TenantId => id;
    }

    private sealed class MemoryStore : IAuditExportObjectStore
    {
        public List<string> Deleted { get; } = [];
        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Deleted.Add(key);
            return Task.CompletedTask;
        }
        public Task PutAsync(string key, byte[] data, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static byte[] Confirmation(Guid tenantId) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        specversion = "1.0", id = Guid.NewGuid(),
        type = ProfileErasureConfirmedV1.EventType,
        source = ProfileErasureConfirmedV1.EventSource,
        time = DateTimeOffset.UtcNow, tenantid = tenantId,
        correlationid = Guid.NewGuid(),
        actor = new { sub = Guid.NewGuid().ToString("D"), type = "machine" },
        data = new { scope = "tenant", tenantId, service = "profile" }
    });

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, Guid tenant)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
