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

public sealed class PersonalExportContributorTests
{
    [Fact]
    public async Task Contribution_contains_only_requested_users_memberships_and_is_idempotent()
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
            postgres.GetConnectionString()) { Username="access_migrator", Password=password }.ConnectionString);
        await migrator.OpenAsync();
        foreach (var migration in new[] { "001_initial.sql", "003_membership_index.sql",
            "004_outbox_publisher.sql", "011_support_grants_rls.sql", "012_audit_export_requests.sql",
            "013_worker_expiry.sql", "016_tenant_deletion_dispatch.sql", "017_tenant_erasure_receipts.sql" })
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, migration)));
        var user = Guid.NewGuid(); var other = Guid.NewGuid();
        var tenant = Guid.NewGuid(); var otherTenant = Guid.NewGuid(); var export = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO tenants(id,slug,name,status) VALUES
              (@tenant,'requested-tenant','Requested','active'),(@otherTenant,'other-tenant','Other','active');
            INSERT INTO identities(user_id,email) VALUES
              (@user,'requested@example.invalid'),(@other,'other@example.invalid');
            INSERT INTO user_tenant_index(user_id,tenant_id,membership_status) VALUES
              (@user,@tenant,'active'),(@other,@otherTenant,'active');
            """, admin))
        {
            seed.Parameters.AddWithValue("user",user); seed.Parameters.AddWithValue("other",other);
            seed.Parameters.AddWithValue("tenant",tenant); seed.Parameters.AddWithValue("otherTenant",otherTenant);
            await seed.ExecuteNonQueryAsync();
        }
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString()) { Username="access_worker", Password=password }.ConnectionString);
        var store = new MemoryExportStore();
        var actor = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["Worker:ActorId"] = actor.ToString("D") }).Build();
        var contributor = new PersonalExportContributor(new AccessWorkerDatabase(source),
            new PostgresEventInbox(), store, configuration);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { specversion="1.0", id=Guid.NewGuid(),
            type=ProfileExportRequestedV1.EventType, source=ProfileExportRequestedV1.EventSource,
            time=DateTimeOffset.UtcNow, correlationid=Guid.NewGuid(),
            actor=new { sub=user.ToString("D"), type="human" }, data=new { userId=user, exportId=export } });
        Assert.True(await contributor.ApplyAsync(request));
        Assert.False(await contributor.ApplyAsync(request));
        var contribution = Encoding.UTF8.GetString((await store.GetAsync(export,"access",default))!);
        Assert.Contains(tenant.ToString("D"), contribution, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(otherTenant.ToString("D"), contribution, StringComparison.OrdinalIgnoreCase);
        await using var emitted = new NpgsqlCommand(
            "SELECT payload::text FROM outbox WHERE type=@type", admin);
        emitted.Parameters.AddWithValue("type", AccessUserExportReadyV1.EventType);
        var envelope = AccessUserExportReadyV1.Parse(Encoding.UTF8.GetBytes(
            (string)(await emitted.ExecuteScalarAsync())!));
        Assert.Equal(export, envelope.Data!.ExportId);
        Assert.Equal(actor.ToString("D"), envelope.Actor!.Sub);
        Assert.Equal(1L, await CountAsync(admin,"SELECT count(*) FROM inbox"));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection,string sql)
    { await using var command=new NpgsqlCommand(sql,connection); await command.ExecuteNonQueryAsync(); }
    private static async Task<long> CountAsync(NpgsqlConnection connection,string sql)
    { await using var command=new NpgsqlCommand(sql,connection); return (long)(await command.ExecuteScalarAsync())!; }
    private sealed class MemoryExportStore : IPrivateExportStore
    {
        private readonly Dictionary<(Guid,string),byte[]> values=[];
        public Task PutAsync(Guid id,string part,byte[] data,CancellationToken ct){values[(id,part)]=data;return Task.CompletedTask;}
        public Task<byte[]?> GetAsync(Guid id,string part,CancellationToken ct)=>Task.FromResult(values.GetValueOrDefault((id,part)));
        public Task DeleteAsync(Guid id,string part,CancellationToken ct){values.Remove((id,part));return Task.CompletedTask;}
    }
}
