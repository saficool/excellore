using System.Security.Cryptography;
using System.Text.Json;
using Excellore.Contracts;
using Excellore.Platform;
using Excellore.Profile.Infrastructure;
using Excellore.Profile.Worker;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Profile.Tests;

public sealed class UserErasureWorkerTests
{
    [Fact]
    public async Task Erasure_removes_user_data_and_objects_but_preserves_restricted_audit()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var postgres = new PostgreSqlBuilder("postgres:16.12").WithPassword(password).Build();
        await postgres.StartAsync();
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString()); await admin.OpenAsync();
        foreach (var role in new[] { "profile_migrator", "profile_app", "profile_publisher", "profile_worker" })
            await ExecuteAsync(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE,CREATE ON SCHEMA public TO profile_migrator");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO profile_app,profile_publisher,profile_worker");
        await using var migrator = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = "profile_migrator", Password = password }.ConnectionString); await migrator.OpenAsync();
        foreach (var migration in new[]{"001_initial.sql","002_worker_projections.sql","003_identity_mirror.sql",
            "004_tenant_erasure.sql","005_tenant_audit_archive.sql","006_personal_exports.sql","007_user_erasure.sql"})
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, migration)));

        var user = Guid.NewGuid(); var other = Guid.NewGuid(); var tenant = Guid.NewGuid();
        var request = Guid.NewGuid(); var upload = Guid.NewGuid(); var export = Guid.NewGuid(); var audit = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO user_profiles(user_id,display_name,avatar_object_key) VALUES
              (@user,'Erase me',@key),(@other,'Keep me',NULL);
            INSERT INTO user_preferences(user_id,key,value) VALUES (@user,'theme','"dark"');
            INSERT INTO tenant_member_profiles(tenant_id,user_id,job_title) VALUES
              (@tenant,@user,'Erase job'),(@tenant,@other,'Keep job');
            INSERT INTO avatar_uploads(id,user_id,object_key,status,content_type,size_bytes)
              VALUES (@upload,@user,@key,'processed','image/jpeg',100);
            INSERT INTO data_export_jobs(id,user_id,status) VALUES (@export,@user,'pending');
            INSERT INTO profile_user_erasure_requests(request_id,user_id) VALUES (@request,@user);
            INSERT INTO profile_audit_events(id,actor_sub,target_sub,action) VALUES
              (@audit,@user,@user,'original-action');
            """, admin))
        {
            seed.Parameters.AddWithValue("user", user); seed.Parameters.AddWithValue("other", other);
            seed.Parameters.AddWithValue("tenant", tenant); seed.Parameters.AddWithValue("request", request);
            seed.Parameters.AddWithValue("upload", upload); seed.Parameters.AddWithValue("export", export);
            seed.Parameters.AddWithValue("audit", audit); seed.Parameters.AddWithValue("key", $"avatars/{upload:N}.webp");
            await seed.ExecuteNonQueryAsync();
        }
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = "profile_worker", Password = password }.ConnectionString);
        var avatarStore = new MemoryAvatarStore(); avatarStore.Values.Add($"avatars/{upload:N}.webp");
        var exportStore = new MemoryExportStore(); await exportStore.PutAsync(export, "complete", [1], default);
        var actor = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { { "Worker:ActorId", actor.ToString("D") } }).Build();
        var projector = new ProfileUserErasureProjector(new ProfileWorkerDatabase(source),
            new PostgresEventInbox(), avatarStore, exportStore, configuration);
        var eventId = Guid.NewGuid();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0",
            id = eventId,
            type = AccessErasureConfirmedV1.EventType,
            source = AccessErasureConfirmedV1.EventSource,
            time = DateTimeOffset.UtcNow,
            correlationid = request,
            actor = new { sub = actor.ToString("D"), type = "machine" },
            data = new { scope = "user", userId = user, service = "access" }
        });

        Assert.True(await projector.ApplyAsync(body)); Assert.False(await projector.ApplyAsync(body));
        Assert.Equal(0L, await CountAsync(admin, "SELECT count(*) FROM user_preferences WHERE user_id=@id", user));
        Assert.Equal(0L, await CountAsync(admin, "SELECT count(*) FROM avatar_uploads WHERE user_id=@id", user));
        Assert.Equal(0L, await CountAsync(admin, "SELECT count(*) FROM data_export_jobs WHERE user_id=@id", user));
        Assert.Equal(0L, await CountAsync(admin, "SELECT count(*) FROM tenant_member_profiles WHERE user_id=@id", user));
        Assert.Equal(1L, await CountAsync(admin, "SELECT count(*) FROM tenant_member_profiles WHERE user_id=@id", other));
        Assert.Equal(DBNull.Value, await ScalarAsync(admin, "SELECT display_name FROM user_profiles WHERE user_id=@id", user));
        Assert.Equal("pending_external_identity_deletion", await ScalarAsync(admin,
            "SELECT status FROM profile_user_erasure_requests WHERE request_id=@id", request));
        Assert.Equal(1L, await CountAsync(admin, "SELECT count(*) FROM profile_audit_events WHERE id=@id", audit));
        Assert.Equal(1L, await CountAsync(admin,
            "SELECT count(*) FROM profile_audit_events WHERE action='profile.user.erasure-marked' AND target_sub=@id", user));
        Assert.Empty(avatarStore.Values); Assert.Null(await exportStore.GetAsync(export, "complete", default));
        Assert.Equal(1L, await CountAsync(admin, "SELECT count(*) FROM outbox WHERE type='profile.erasure.confirmed.v1'"));
        await using (var seedTombstone = new NpgsqlCommand(
            "INSERT INTO profile_tenant_tombstones(tenant_id) VALUES (@tenant)", admin))
        { seedTombstone.Parameters.AddWithValue("tenant", tenant); await seedTombstone.ExecuteNonQueryAsync(); }
        var tenantConfirmationId = Guid.NewGuid();
        var tenantConfirmation = JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0",
            id = tenantConfirmationId,
            type = AccessErasureConfirmedV1.EventType,
            source = AccessErasureConfirmedV1.EventSource,
            time = DateTimeOffset.UtcNow,
            tenantid = tenant,
            correlationid = Guid.NewGuid(),
            actor = new { sub = actor.ToString("D"), type = "machine" },
            data = new { scope = "tenant", tenantId = tenant, service = "access" }
        });
        Assert.True(await projector.ApplyAsync(tenantConfirmation));
        Assert.False(await projector.ApplyAsync(tenantConfirmation));
    }

    private static async Task ExecuteAsync(NpgsqlConnection c, string sql)
    { await using var command = new NpgsqlCommand(sql, c); await command.ExecuteNonQueryAsync(); }
    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql, Guid id)
    { await using var command = new NpgsqlCommand(sql, c); command.Parameters.AddWithValue("id", id); return await command.ExecuteScalarAsync(); }
    private static async Task<long> CountAsync(NpgsqlConnection c, string sql, Guid id)
    { return (long)(await ScalarAsync(c, sql, id))!; }
    private static async Task<long> CountAsync(NpgsqlConnection c, string sql)
    { await using var command = new NpgsqlCommand(sql, c); return (long)(await command.ExecuteScalarAsync())!; }
    private sealed class MemoryAvatarStore : IAvatarObjectStore
    { public HashSet<string> Values { get; } = []; public Task PutAsync(string k, byte[] d, CancellationToken c) { Values.Add(k); return Task.CompletedTask; } public Task<byte[]?> GetAsync(string k, CancellationToken c) => Task.FromResult<byte[]?>(Values.Contains(k) ? [1] : null); public Task DeleteAsync(string k, CancellationToken c) { Values.Remove(k); return Task.CompletedTask; } }
    private sealed class MemoryExportStore : IPrivateExportStore
    { private readonly Dictionary<(Guid, string), byte[]> values = []; public Task PutAsync(Guid i, string p, byte[] d, CancellationToken c) { values[(i, p)] = d; return Task.CompletedTask; } public Task<byte[]?> GetAsync(Guid i, string p, CancellationToken c) => Task.FromResult(values.GetValueOrDefault((i, p))); public Task DeleteAsync(Guid i, string p, CancellationToken c) { values.Remove((i, p)); return Task.CompletedTask; } }
}
