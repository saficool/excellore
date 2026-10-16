using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Excellore.Contracts;
using Excellore.Platform;
using Excellore.Profile.Worker;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Profile.Tests;

public sealed class TenantExportWorkerTests
{
    [Fact]
    public async Task Tenant_contribution_is_isolated_idempotent_and_machine_attributed()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var postgres = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(password).Build();
        await postgres.StartAsync();
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        foreach (var role in new[] { "profile_migrator", "profile_app", "profile_publisher", "profile_worker" })
            await ExecuteAsync(admin,
                $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE,CREATE ON SCHEMA public TO profile_migrator");
        await ExecuteAsync(admin,
            "GRANT USAGE ON SCHEMA public TO profile_app,profile_publisher,profile_worker");
        await using var migrator = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString())
        { Username = "profile_migrator", Password = password }.ConnectionString);
        await migrator.OpenAsync();
        foreach (var migration in new[]
        {
            "001_initial.sql", "002_worker_projections.sql", "003_identity_mirror.sql",
            "004_tenant_erasure.sql", "005_tenant_audit_archive.sql", "006_personal_exports.sql"
        })
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, migration)));

        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var export = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO tenant_member_profiles(tenant_id,user_id,job_title,status) VALUES
              (@tenant,@user,'Exported profile','active'),
              (@otherTenant,@otherUser,'Private profile','active');
            """, admin))
        {
            seed.Parameters.AddWithValue("tenant", tenant);
            seed.Parameters.AddWithValue("otherTenant", otherTenant);
            seed.Parameters.AddWithValue("user", user);
            seed.Parameters.AddWithValue("otherUser", otherUser);
            await seed.ExecuteNonQueryAsync();
        }
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString())
        { Username = "profile_worker", Password = password }.ConnectionString);
        var objects = new MemoryExportStore();
        var actor = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Worker:ActorId"] = actor.ToString("D") }).Build();
        var projector = new ProfileTenantExportProjector(new ProfileWorkerDatabase(source),
            new PostgresEventInbox(), objects, configuration);
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0",
            id = Guid.NewGuid(),
            type = TenantExportRequestedV1.EventType,
            source = TenantExportRequestedV1.EventSource,
            time = DateTimeOffset.UtcNow,
            tenantid = tenant,
            correlationid = Guid.NewGuid(),
            actor = new { sub = user.ToString("D"), type = "human" },
            data = new { tenantId = tenant, exportId = export }
        });
        Assert.True(await projector.ApplyAsync(request));
        Assert.False(await projector.ApplyAsync(request));
        var profile = Encoding.UTF8.GetString((await objects.GetAsync(export, "profile", default))!);
        Assert.Contains("Exported profile", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("Private profile", profile, StringComparison.Ordinal);
        await using var emitted = new NpgsqlCommand(
            "SELECT payload::text FROM outbox WHERE type=@type", admin);
        emitted.Parameters.AddWithValue("type", ProfileTenantExportReadyV1.EventType);
        var envelope = ProfileTenantExportReadyV1.Parse(Encoding.UTF8.GetBytes(
            (string)(await emitted.ExecuteScalarAsync())!));
        Assert.Equal(export, envelope.Data!.ExportId);
        Assert.Equal(actor.ToString("D"), envelope.Actor!.Sub);
        Assert.Equal("machine", envelope.Actor.Type);
        Assert.Equal(1L, await CountAsync(admin, "SELECT count(*) FROM inbox"));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
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
