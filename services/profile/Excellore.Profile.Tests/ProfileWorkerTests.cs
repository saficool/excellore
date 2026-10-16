using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Excellore.Contracts;
using Excellore.Platform;
using Excellore.Profile.Worker;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Profile.Tests;

public sealed class ProfileWorkerTests
{
    [Fact]
    public async Task Membership_events_are_tenant_bound_atomic_and_idempotent()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var postgres = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await postgres.StartAsync();
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"CREATE ROLE profile_migrator LOGIN PASSWORD '{adminPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "CREATE ROLE profile_app LOGIN NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, $"CREATE ROLE profile_publisher LOGIN PASSWORD '{adminPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, $"CREATE ROLE profile_worker LOGIN PASSWORD '{adminPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin,
            "GRANT USAGE,CREATE ON SCHEMA public TO profile_migrator; " +
            "GRANT USAGE ON SCHEMA public TO profile_app,profile_publisher,profile_worker");
        await using var migrator = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString()) { Username = "profile_migrator", Password = adminPassword }.ConnectionString);
        await migrator.OpenAsync();
        foreach (var migration in new[] { "001_initial.sql", "002_worker_projections.sql" })
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, migration)));

        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString()) { Username = "profile_worker", Password = adminPassword }.ConnectionString);
        var projector = new ProfileMembershipProjector(new ProfileWorkerDatabase(source),
            new PostgresEventInbox());
        var firstTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var added = Event(TenantMemberAddedV1.EventType, firstTenant, userId, true);
        Assert.True(await projector.ApplyAsync(TenantMemberAddedV1.EventType, added));
        Assert.False(await projector.ApplyAsync(TenantMemberAddedV1.EventType, added));
        Assert.Equal(1L, await CountAsync(admin, firstTenant));
        Assert.Equal(0L, await CountAsync(admin, otherTenant));

        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var scope = new NpgsqlCommand(
                "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction);
            scope.Parameters.AddWithValue("tenant", otherTenant.ToString("D"));
            await scope.ExecuteNonQueryAsync();
            await using var query = new NpgsqlCommand(
                "SELECT count(*) FROM tenant_member_profiles WHERE tenant_id=@first", connection,
                transaction);
            query.Parameters.AddWithValue("first", firstTenant);
            Assert.Equal(0L, (long)(await query.ExecuteScalarAsync())!);
            await transaction.CommitAsync();
        }

        Assert.True(await projector.ApplyAsync(TenantMemberSuspendedV1.EventType,
            Event(TenantMemberSuspendedV1.EventType, firstTenant, userId)));
        Assert.Equal("suspended", await StatusAsync(admin, firstTenant, userId));
        Assert.True(await projector.ApplyAsync(TenantMemberReactivatedV1.EventType,
            Event(TenantMemberReactivatedV1.EventType, firstTenant, userId)));
        Assert.Equal("active", await StatusAsync(admin, firstTenant, userId));
        Assert.True(await projector.ApplyAsync(TenantMemberRemovedV1.EventType,
            Event(TenantMemberRemovedV1.EventType, firstTenant, userId)));
        Assert.Equal(0L, await CountAsync(admin, firstTenant));
        Assert.Equal(4L, await ScalarAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.False(await ScalarBoolAsync(source,
            "SELECT rolbypassrls FROM pg_roles WHERE rolname=current_user"));
        Assert.False(await ScalarBoolAsync(source,
            "SELECT has_table_privilege(current_user,'user_profiles','SELECT')"));

        var outboxId = Guid.NewGuid();
        var globalEvent = JsonSerializer.Serialize(new
        {
            specversion = "1.0", id = outboxId, type = ProfileUpdatedV1.EventType,
            source = "excellore/profile", time = DateTimeOffset.UtcNow,
            correlationid = Guid.NewGuid(), actor = new { sub = userId.ToString("D"), type = "human" },
            data = new { userId, changed = new[] { "preferences" } }
        });
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO outbox (id,type,payload)
            VALUES (@id,@type,@payload::jsonb)
            """, admin))
        {
            insert.Parameters.AddWithValue("id", outboxId);
            insert.Parameters.AddWithValue("type", ProfileUpdatedV1.EventType);
            insert.Parameters.AddWithValue("payload", globalEvent);
            await insert.ExecuteNonQueryAsync();
        }
        await using var publisherSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString()) { Username = "profile_publisher", Password = adminPassword }.ConnectionString);
        await using var publisherConnection = await publisherSource.OpenConnectionAsync();
        var publisher = new CapturePublisher();
        var dispatcher = new PostgresOutboxDispatcher(globalEventsOnly: true);
        Assert.True(await dispatcher.DispatchOnceAsync(publisherConnection, publisher));
        Assert.False(await dispatcher.DispatchOnceAsync(publisherConnection, publisher));
        Assert.Equal(outboxId, publisher.Id);
        Assert.Equal(userId, ProfileUpdatedV1.Parse(publisher.Body).Data!.UserId);
        Assert.Equal(1L, await ScalarAsync(admin,
            "SELECT count(*) FROM outbox WHERE published_at IS NOT NULL"));
    }

    private static byte[] Event(string type, Guid tenantId, Guid userId, bool added = false)
    {
        var data = added ? new { tenantId, userId, membershipId = Guid.NewGuid() } :
            new { tenantId, userId, membershipId = Guid.Empty };
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0", id = Guid.NewGuid(), type, source = "excellore/access",
            time = DateTimeOffset.UtcNow, tenantid = tenantId, correlationid = Guid.NewGuid(),
            actor = new { sub = Guid.NewGuid().ToString("D"), type = "machine" }, data
        });
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ScalarBoolAsync(NpgsqlDataSource source, string sql)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM tenant_member_profiles WHERE tenant_id=@tenant", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> StatusAsync(NpgsqlConnection connection, Guid tenantId, Guid userId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT status FROM tenant_member_profiles WHERE tenant_id=@tenant AND user_id=@user",
            connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", userId);
        return (string?)await command.ExecuteScalarAsync();
    }

    private sealed class CapturePublisher : IEventPublisher
    {
        public Guid Id { get; private set; }
        public byte[] Body { get; private set; } = [];
        public Task PublishAsync(Guid eventId, string eventType, ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(ProfileUpdatedV1.EventType, eventType);
            Id = eventId;
            Body = body.ToArray();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
