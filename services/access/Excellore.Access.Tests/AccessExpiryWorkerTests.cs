using System.Security.Cryptography;
using Excellore.Access.Infrastructure;
using Excellore.Access.Worker;
using Excellore.Auth;
using Excellore.Authz;
using Excellore.Platform;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace Excellore.Access.Tests;

public sealed class AccessExpiryWorkerTests
{
    [Fact]
    public async Task Expiry_is_atomic_tenant_scoped_idempotent_and_invalidates_authority()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var postgres = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(password).Build();
        await postgres.StartAsync();
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        foreach (var role in new[] { "access_migrator", "access_app", "access_publisher", "access_worker" })
            await ExecAsync(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
        await ExecAsync(admin, "GRANT USAGE,CREATE ON SCHEMA public TO access_migrator");
        await ExecAsync(admin, "GRANT USAGE ON SCHEMA public TO access_app,access_publisher,access_worker");
        await using var migrator = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString()) { Username = "access_migrator", Password = password }.ConnectionString);
        await migrator.OpenAsync();
        foreach (var migration in new[]
        {
            "001_initial.sql", "003_membership_index.sql", "004_outbox_publisher.sql",
            "005_audit_append_only.sql", "011_support_grants_rls.sql",
            "012_audit_export_requests.sql", "013_worker_expiry.sql",
            "014_worker_identity_drift.sql"
        })
            await ExecAsync(migrator, await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, migration)));

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var member = Guid.NewGuid();
        var staff = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO tenants(id,slug,name,status) VALUES
              (@first,'worker-first','First','active'),(@second,'worker-second','Second','active');
            INSERT INTO identities(user_id,email,status) VALUES
              (@member,'member@invalid.test','active'),(@staff,'staff@invalid.test','active');
            INSERT INTO roles(id,tenant_id,name,is_system) VALUES (@role,NULL,'worker-test',true);
            INSERT INTO tenant_members(tenant_id,user_id,status) VALUES
              (@first,@member,'active'),(@second,@member,'active');
            INSERT INTO tenant_members(tenant_id,user_id,status,is_support,support_grant_id,expires_at)
              VALUES (@first,@staff,'active',true,@grant,now()-interval '1 minute');
            INSERT INTO role_assignments(id,tenant_id,user_id,role_id,expires_at,granted_by)
              VALUES (gen_random_uuid(),@first,@member,@role,now()-interval '1 minute',@member),
                (gen_random_uuid(),@second,@member,@role,now()+interval '1 day',@member),
                (gen_random_uuid(),@first,@staff,@role,now()-interval '1 minute',@member);
            INSERT INTO support_grants(id,tenant_id,staff_user_id,role_id,reason,status,expires_at)
              VALUES (@grant,@first,@staff,@role,'test','active',now()-interval '1 minute');
            """, admin))
        {
            seed.Parameters.AddWithValue("first", first);
            seed.Parameters.AddWithValue("second", second);
            seed.Parameters.AddWithValue("member", member);
            seed.Parameters.AddWithValue("staff", staff);
            seed.Parameters.AddWithValue("role", roleId);
            seed.Parameters.AddWithValue("grant", grantId);
            await seed.ExecuteNonQueryAsync();
        }

        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString()) { Username = "access_worker", Password = password }.ConnectionString);
        var service = new AccessExpiryService(source, new FixedTenant(first));
        Assert.Equal(3, await service.ExpireAsync(Guid.NewGuid()));
        Assert.Equal(0, await service.ExpireAsync(Guid.NewGuid()));
        Assert.Equal(0L, await ScalarAsync(admin,
            "SELECT count(*) FROM role_assignments WHERE tenant_id=@tenant AND revoked_at IS NULL", first));
        Assert.Equal(1L, await ScalarAsync(admin,
            "SELECT count(*) FROM role_assignments WHERE tenant_id=@tenant AND revoked_at IS NULL", second));
        Assert.Equal(1L, await ScalarAsync(admin,
            "SELECT authz_version FROM tenants WHERE id=@tenant", first) - 1);
        Assert.Equal(1L, await ScalarAsync(admin,
            "SELECT count(*) FROM outbox WHERE tenant_id=@tenant AND type='access.authz.changed.v1'", first));
        Assert.Equal(1L, await ScalarAsync(admin,
            "SELECT count(*) FROM support_grants WHERE tenant_id=@tenant AND status='expired'", first));
        Assert.Equal(1L, await ScalarAsync(admin,
            "SELECT count(*) FROM tenant_members WHERE tenant_id=@tenant AND is_support AND status='removed'", first));
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var scope = new NpgsqlCommand(
                "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction);
            scope.Parameters.AddWithValue("tenant", second.ToString("D"));
            await scope.ExecuteNonQueryAsync();
            await using var query = new NpgsqlCommand(
                "SELECT count(*) FROM role_assignments WHERE tenant_id=@first", connection, transaction);
            query.Parameters.AddWithValue("first", first);
            Assert.Equal(0L, (long)(await query.ExecuteScalarAsync())!);
            await transaction.CommitAsync();
        }
        Assert.False(await BoolAsync(source,
            "SELECT rolbypassrls FROM pg_roles WHERE rolname=current_user"));
        Assert.False(await BoolAsync(source,
            "SELECT has_table_privilege(current_user,'identities','UPDATE')"));
        var drift = new AccessIdentityDriftWorker(new AccessWorkerDatabase(source),
            new FakeDirectory(member, staff), NullLogger<AccessIdentityDriftWorker>.Instance);
        Assert.Equal(1, await drift.CountDriftAsync());
        Assert.False(await BoolAsync(source,
            "SELECT has_column_privilege(current_user,'identities','email','SELECT')"));

        var exportId = Guid.NewGuid();
        await using (var create = new NpgsqlCommand("""
            INSERT INTO audit_exports (id,tenant_id,requested_by,status)
            VALUES (@id,@tenant,@actor,'pending')
            """, admin))
        {
            create.Parameters.AddWithValue("id", exportId);
            create.Parameters.AddWithValue("tenant", first);
            create.Parameters.AddWithValue("actor", member);
            await create.ExecuteNonQueryAsync();
        }
        var objects = new MemoryAuditStore();
        var builder = new AuditExportBuilder(source, new FixedTenant(first), objects);
        Assert.True(await builder.ProcessOneAsync(Guid.NewGuid()));
        Assert.False(await builder.ProcessOneAsync(Guid.NewGuid()));
        Assert.Equal(1L, await ScalarAsync(admin,
            "SELECT count(*) FROM audit_exports WHERE tenant_id=@tenant AND status='ready'", first));
        Assert.Contains("access.assignment.expired", objects.Text);
        Assert.DoesNotContain(second.ToString("D"), objects.Text, StringComparison.OrdinalIgnoreCase);

        await using var appSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString()) { Username = "access_app", Password = password }.ConnectionString);
        var download = new AuditExportDownloadService(appSource,
            new FixedTenantContext(first), new FixedUser(member, true), objects);
        var ready = await download.DownloadAsync(exportId);
        Assert.Equal(AuditExportDownloadStatus.Ready, ready.Status);
        Assert.Equal(objects.Data, ready.Data);
        var wrongTenant = new AuditExportDownloadService(appSource,
            new FixedTenantContext(second), new FixedUser(member, true), objects);
        Assert.Equal(AuditExportDownloadStatus.NotFound,
            (await wrongTenant.DownloadAsync(exportId)).Status);
        var noStepUp = new AuditExportDownloadService(appSource,
            new FixedTenantContext(first), new FixedUser(member, false), objects);
        Assert.Equal(AuditExportDownloadStatus.StepUpRequired,
            (await noStepUp.DownloadAsync(exportId)).Status);
    }

    private sealed class FixedTenant(Guid tenantId) : ITenantIdAccessor
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContextAccessor
    {
        public Guid TenantId { get; } = tenantId;
        public ITenantContext? Current => null;
        public void Set(Guid id, AuthzSnapshot snapshot) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
    }

    private sealed class FixedUser(Guid id, bool hasRecentMfa) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string? Email => null;
        public string? Acr => null;
        public bool HasRecentMfa => hasRecentMfa;
    }

    private sealed class MemoryAuditStore : IAuditExportObjectStore
    {
        public byte[] Data { get; private set; } = [];
        public string Text => System.Text.Encoding.UTF8.GetString(Data);
        public Task PutAsync(string key, byte[] data, CancellationToken cancellationToken)
        {
            Data = data;
            return Task.CompletedTask;
        }
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(Data);
    }

    private sealed class FakeDirectory(Guid active, Guid disabled) : IKeycloakDirectory
    {
        public Task<IReadOnlyDictionary<Guid, KeycloakDirectoryUser>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, KeycloakDirectoryUser>>(
                new Dictionary<Guid, KeycloakDirectoryUser>
                {
                    [active] = new(active, true, null, null, null, null),
                    [disabled] = new(disabled, false, null, null, null, null)
                });
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, Guid tenantId)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> BoolAsync(NpgsqlDataSource source, string sql)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
