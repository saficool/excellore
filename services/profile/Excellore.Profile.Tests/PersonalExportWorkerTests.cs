using System.Security.Cryptography;
using System.Text.Json;
using Excellore.Contracts;
using Excellore.Platform;
using Excellore.Profile.Worker;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Profile.Tests;

public sealed class PersonalExportWorkerTests
{
    [Fact]
    public async Task Profile_contribution_is_user_bounded_and_final_download_excludes_audit()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var postgres = new PostgreSqlBuilder("postgres:16.12").WithPassword(password).Build();
        await postgres.StartAsync();
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        foreach (var role in new[] { "profile_migrator", "profile_app", "profile_publisher", "profile_worker" })
            await ExecuteAsync(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE,CREATE ON SCHEMA public TO profile_migrator");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO profile_app,profile_publisher,profile_worker");
        await using var migrator = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString()) { Username="profile_migrator", Password=password }.ConnectionString);
        await migrator.OpenAsync();
        foreach (var migration in new[] { "001_initial.sql", "002_worker_projections.sql",
            "003_identity_mirror.sql", "004_tenant_erasure.sql", "005_tenant_audit_archive.sql",
            "006_personal_exports.sql" })
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, migration)));
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        var export = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO user_profiles(user_id,display_name) VALUES (@user,'Requested'),(@other,'Other');
            INSERT INTO user_preferences(user_id,key,value) VALUES (@user,'theme','"dark"'::jsonb);
            INSERT INTO tenant_member_profiles(tenant_id,user_id,job_title)
              VALUES (@tenant,@user,'Engineer'),(@tenant,@other,'Private');
            INSERT INTO data_export_jobs(id,user_id,status) VALUES (@export,@user,'pending');
            """, admin))
        {
            seed.Parameters.AddWithValue("user", user); seed.Parameters.AddWithValue("other", other);
            seed.Parameters.AddWithValue("tenant", tenant); seed.Parameters.AddWithValue("export", export);
            await seed.ExecuteNonQueryAsync();
        }
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString()) { Username="profile_worker", Password=password }.ConnectionString);
        var store = new MemoryExportStore();
        var projector = new ProfilePersonalExportProjector(new ProfileWorkerDatabase(source),
            new PostgresEventInbox(), store);
        var request = Event(ProfileExportRequestedV1.EventType, "excellore/profile", user, export, "human");
        Assert.True(await projector.BuildProfileAsync(request));
        Assert.False(await projector.BuildProfileAsync(request));
        var profile = JsonDocument.Parse((await store.GetAsync(export, "profile", default))!);
        Assert.Equal("Requested", profile.RootElement.GetProperty("profile").GetProperty("display_name").GetString());
        Assert.Single(profile.RootElement.GetProperty("tenantProfiles").EnumerateArray());
        Assert.DoesNotContain(other.ToString("D"), profile.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        await store.PutAsync(export, "access", JsonSerializer.SerializeToUtf8Bytes(new
            { memberships = new[] { new { tenantId = tenant, status = "active" } } }), default);
        var ready = Event(AccessUserExportReadyV1.EventType, "excellore/access", user, export, "machine");
        Assert.True(await projector.CompleteAsync(ready));
        Assert.False(await projector.CompleteAsync(ready));
        var complete = JsonDocument.Parse((await store.GetAsync(export, "complete", default))!);
        Assert.False(complete.RootElement.GetProperty("auditRecords").GetProperty("included").GetBoolean());
        Assert.Equal("active", complete.RootElement.GetProperty("access").GetProperty("memberships")[0]
            .GetProperty("status").GetString());
        await using var status = new NpgsqlCommand(
            "SELECT status FROM data_export_jobs WHERE id=@id", admin);
        status.Parameters.AddWithValue("id", export);
        Assert.Equal("ready", await status.ExecuteScalarAsync());
        await using var emitted = new NpgsqlCommand(
            "SELECT payload::text FROM outbox WHERE type=@type", admin);
        emitted.Parameters.AddWithValue("type", ProfileExportReadyV1.EventType);
        Assert.Equal(export, ProfileExportReadyV1.Parse(System.Text.Encoding.UTF8.GetBytes(
            (string)(await emitted.ExecuteScalarAsync())!)).Data!.ExportId);
    }

    private static byte[] Event(string type, string source, Guid user, Guid export, string actorType) =>
        JsonSerializer.SerializeToUtf8Bytes(new { specversion="1.0", id=Guid.NewGuid(), type,
            source, time=DateTimeOffset.UtcNow, correlationid=Guid.NewGuid(),
            actor=new { sub=Guid.NewGuid().ToString("D"), type=actorType },
            data=new { userId=user, exportId=export } });
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
    private sealed class MemoryExportStore : IPrivateExportStore
    {
        private readonly Dictionary<(Guid,string),byte[]> values = [];
        public Task PutAsync(Guid id,string part,byte[] data,CancellationToken ct) { values[(id,part)]=data; return Task.CompletedTask; }
        public Task<byte[]?> GetAsync(Guid id,string part,CancellationToken ct) => Task.FromResult(values.GetValueOrDefault((id,part)));
        public Task DeleteAsync(Guid id,string part,CancellationToken ct) { values.Remove((id,part)); return Task.CompletedTask; }
    }
}
