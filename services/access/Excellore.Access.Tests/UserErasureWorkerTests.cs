using System.Security.Cryptography;
using System.Text.Json;
using Excellore.Access.Infrastructure;
using Excellore.Access.Worker;
using Excellore.Contracts;
using Excellore.Platform;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Access.Tests;

public sealed class UserErasureWorkerTests
{
    [Fact]
    public async Task Erasure_revokes_only_target_user_preserves_audits_and_is_idempotent()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var postgres = new PostgreSqlBuilder("postgres:16.12").WithPassword(password).Build();
        await postgres.StartAsync();
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        foreach (var role in new[] { "access_migrator", "access_app", "access_publisher", "access_worker" })
            await ExecuteAsync(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE,CREATE ON SCHEMA public TO access_migrator");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO access_app,access_publisher,access_worker");
        await using var migrator = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString())
        { Username = "access_migrator", Password = password }.ConnectionString);
        await migrator.OpenAsync();
        foreach (var migration in new[]
        {
            "001_initial.sql", "003_membership_index.sql", "004_outbox_publisher.sql",
            "005_audit_append_only.sql", "010_ownership_transfers.sql", "011_support_grants_rls.sql",
            "012_audit_export_requests.sql", "013_worker_expiry.sql", "015_role_assignment_batches.sql",
            "016_tenant_deletion_dispatch.sql", "017_tenant_erasure_receipts.sql",
            "018_tenant_erasure_finalization.sql", "019_tenant_export_requests.sql",
            "020_tenant_export_worker.sql", "021_user_erasure.sql"
        })
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, migration)));

        var tenant = Guid.NewGuid(); var otherTenant = Guid.NewGuid();
        var user = Guid.NewGuid(); var other = Guid.NewGuid(); var roleId = Guid.NewGuid();
        var otherRole = Guid.NewGuid(); var originalAudit = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO tenants(id,slug,name,status) VALUES
              (@tenant,'erase-one','Erase one','active'),(@otherTenant,'keep-two','Keep two','active');
            INSERT INTO identities(user_id,email,given_name) VALUES
              (@user,'erase@example.invalid','Erase'),(@other,'keep@example.invalid','Keep');
            INSERT INTO user_tenant_index(user_id,tenant_id,membership_status) VALUES
              (@user,@tenant,'active'),(@other,@tenant,'active'),(@other,@otherTenant,'active');
            INSERT INTO tenant_members(tenant_id,user_id,status,invited_by) VALUES
              (@tenant,@user,'active',@other),(@tenant,@other,'active',@user),
              (@otherTenant,@other,'active',@other);
            INSERT INTO roles(id,tenant_id,name,is_system,created_by) VALUES
              (@role,@tenant,'Retained role',false,@user),(@otherRole,@otherTenant,'Other role',false,@other);
            INSERT INTO role_assignments(id,tenant_id,user_id,role_id,granted_by) VALUES
              (gen_random_uuid(),@tenant,@user,@role,@other),
              (gen_random_uuid(),@tenant,@other,@role,@user),
              (gen_random_uuid(),@otherTenant,@other,@otherRole,@other);
            INSERT INTO audit_events(id,tenant_id,actor_sub,actor_type,action,hash,hash_version)
              VALUES (@audit,@tenant,@user,'human','original-action',decode('01','hex'),2);
            """, admin))
        {
            seed.Parameters.AddWithValue("tenant", tenant); seed.Parameters.AddWithValue("otherTenant", otherTenant);
            seed.Parameters.AddWithValue("user", user); seed.Parameters.AddWithValue("other", other);
            seed.Parameters.AddWithValue("role", roleId); seed.Parameters.AddWithValue("otherRole", otherRole);
            seed.Parameters.AddWithValue("audit", originalAudit); await seed.ExecuteNonQueryAsync();
        }
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString())
        { Username = "access_worker", Password = password }.ConnectionString);
        var actor = Guid.NewGuid();
        var projector = new AccessUserErasureProjector(source, actor);
        var requestId = Guid.NewGuid();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0",
            id = requestId,
            type = ProfileDeletionRequestedV1.EventType,
            source = "excellore/profile",
            time = DateTimeOffset.UtcNow,
            correlationid = requestId,
            actor = new { sub = user.ToString("D"), type = "human" },
            data = new { userId = user }
        });

        Assert.True(await projector.ApplyAsync(body));
        Assert.False(await projector.ApplyAsync(body));
        Assert.Equal("deleted", await ScalarAsync(admin, "SELECT status FROM identities WHERE user_id=@id", user));
        Assert.Equal(0L, await CountAsync(admin, "SELECT count(*) FROM tenant_members WHERE user_id=@id", user));
        Assert.Equal(1L, await CountAsync(admin, "SELECT count(*) FROM tenant_members WHERE tenant_id=@tenant AND user_id=@id", tenant, other));
        Assert.Equal(1L, await CountAsync(admin, "SELECT count(*) FROM tenant_members WHERE tenant_id=@id", otherTenant));
        var tombstone = (Guid)(await ScalarAsync(admin,
            "SELECT tombstone_id FROM user_erasure_requests WHERE user_id=@id", user))!;
        Assert.Equal(tombstone, await ScalarAsync(admin, "SELECT created_by FROM roles WHERE id=@id", roleId));
        Assert.Equal(tombstone, await ScalarAsync(admin,
            "SELECT granted_by FROM role_assignments WHERE tenant_id=@tenant AND user_id=@id", tenant, other));
        Assert.Equal(user, await ScalarAsync(admin, "SELECT actor_sub FROM audit_events WHERE id=@id", originalAudit));
        Assert.Equal(1L, await CountAsync(admin,
            "SELECT count(*) FROM audit_events WHERE tenant_id=@id AND action='access.user.erasure-marked'", tenant));
        Assert.Equal(1L, await CountAsync(admin,
            "SELECT count(*) FROM outbox WHERE type='access.erasure.confirmed.v1'"));
        Assert.Equal(1L, await CountAsync(admin, "SELECT count(*) FROM inbox WHERE event_id=@id", requestId));
        var profileEventId = Guid.NewGuid();
        var profileConfirmation = JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0",
            id = profileEventId,
            type = ProfileErasureConfirmedV1.EventType,
            source = ProfileErasureConfirmedV1.EventSource,
            time = DateTimeOffset.UtcNow,
            correlationid = requestId,
            actor = new { sub = actor.ToString("D"), type = "machine" },
            data = new { scope = "user", userId = user, service = "profile" }
        });
        var confirmation = new ProfileErasureConfirmationProjector(
            new AccessWorkerDatabase(source), new PostgresEventInbox());
        Assert.True(await confirmation.ApplyAsync(profileConfirmation));
        Assert.False(await confirmation.ApplyAsync(profileConfirmation));
        Assert.Equal("pending_external_identity_deletion", await ScalarAsync(admin,
            "SELECT status FROM user_erasure_requests WHERE request_id=@id", requestId));
        Assert.Equal(1L, await CountAsync(admin,
            "SELECT count(*) FROM user_erasure_requests WHERE request_id=@id AND profile_confirmed_at IS NOT NULL",
            requestId));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, Guid id)
    { await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("id", id); return await command.ExecuteScalarAsync(); }
    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, Guid tenant, Guid id)
    { await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("tenant", tenant); command.Parameters.AddWithValue("id", id); return await command.ExecuteScalarAsync(); }
    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, Guid id)
    { var value = await ScalarAsync(connection, sql, id); return (long)value!; }
    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, Guid tenant, Guid id)
    { await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("tenant", tenant); command.Parameters.AddWithValue("id", id); return (long)(await command.ExecuteScalarAsync())!; }
    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    { await using var command = new NpgsqlCommand(sql, connection); return (long)(await command.ExecuteScalarAsync())!; }
}
