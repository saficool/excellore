using System.Security.Cryptography;
using System.Text.Json;
using Excellore.Platform;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Platform.Tests;

public sealed class PostgresOutboxDispatcherTests
{
    [Fact]
    public async Task Confirmation_marks_one_row_and_failed_or_ambiguous_publish_remains_retryable()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var publisherPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin,
            $"CREATE ROLE outbox_publisher LOGIN PASSWORD '{publisherPassword}' NOSUPERUSER NOBYPASSRLS;");
        await ExecuteAsync(admin, """
            CREATE TABLE outbox (
                id uuid PRIMARY KEY,
                type text NOT NULL,
                tenant_id uuid,
                payload jsonb NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                published_at timestamptz
            );
            COMMENT ON TABLE outbox IS 'Class P, RLS-exempt: routing metadata';
            GRANT USAGE ON SCHEMA public TO outbox_publisher;
            GRANT SELECT, UPDATE (published_at) ON outbox TO outbox_publisher;
            """);
        var publisherConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "outbox_publisher",
            Password = publisherPassword
        }.ConnectionString;
        await using var publisherConnection = new NpgsqlConnection(publisherConnectionString);
        await publisherConnection.OpenAsync();
        var dispatcher = new PostgresOutboxDispatcher();
        var publisher = new RecordingPublisher();
        var tenantId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        await SeedAsync(admin, firstId, tenantId);
        await using (var command = new NpgsqlCommand(
                         "UPDATE outbox SET type = 'other.v1' WHERE id = @id", publisherConnection))
        {
            command.Parameters.AddWithValue("id", firstId);
            await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        }

        publisher.FailAfterRecording = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchOnceAsync(publisherConnection, publisher));
        Assert.False(await IsPublishedAsync(admin, firstId));
        Assert.True(await dispatcher.DispatchOnceAsync(publisherConnection, publisher));
        Assert.True(await IsPublishedAsync(admin, firstId));
        Assert.Equal(new[] { firstId, firstId }, publisher.EventIds);
        Assert.False(await dispatcher.DispatchOnceAsync(publisherConnection, publisher));

        var lockedId = Guid.NewGuid();
        var availableId = Guid.NewGuid();
        await SeedAsync(admin, lockedId, tenantId);
        await SeedAsync(admin, availableId, tenantId);
        await using (var lockTransaction = await admin.BeginTransactionAsync())
        {
            await using (var command = new NpgsqlCommand(
                             "SELECT id FROM outbox WHERE id = @id FOR UPDATE", admin,
                             lockTransaction))
            {
                command.Parameters.AddWithValue("id", lockedId);
                Assert.Equal(lockedId, (Guid)(await command.ExecuteScalarAsync())!);
            }
            Assert.True(await dispatcher.DispatchOnceAsync(publisherConnection, publisher));
            Assert.Equal(availableId, publisher.EventIds[^1]);
            Assert.False(await IsPublishedAsync(admin, lockedId, lockTransaction));
            await lockTransaction.CommitAsync();
        }
        Assert.True(await dispatcher.DispatchOnceAsync(publisherConnection, publisher));
        Assert.Equal(lockedId, publisher.EventIds[^1]);
        Assert.False(await dispatcher.DispatchOnceAsync(publisherConnection, publisher));
        Assert.True(await IsPublishedAsync(admin, availableId));
        Assert.True(await IsPublishedAsync(admin, lockedId));

        var mismatchedId = Guid.NewGuid();
        await using (var command = new NpgsqlCommand(
                         "INSERT INTO outbox (id, type, tenant_id, payload) " +
                         "VALUES (@id, 'profile.updated.v1', @tenant_id, CAST(@payload AS jsonb))", admin))
        {
            command.Parameters.AddWithValue("id", mismatchedId);
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new
            {
                specversion = "1.0",
                id = mismatchedId,
                type = "profile.updated.v1",
                tenantid = Guid.NewGuid()
            }));
            await command.ExecuteNonQueryAsync();
        }
        var publishedCount = publisher.EventIds.Count;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            dispatcher.DispatchOnceAsync(publisherConnection, publisher));
        Assert.Equal(publishedCount, publisher.EventIds.Count);
        Assert.False(await IsPublishedAsync(admin, mismatchedId));
    }

    private static async Task SeedAsync(NpgsqlConnection connection, Guid eventId, Guid tenantId)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await new PostgresEventOutbox().EnqueueAsync(transaction, eventId, "profile.updated.v1",
            tenantId, JsonSerializer.SerializeToUtf8Bytes(new
            {
                specversion = "1.0",
                id = eventId,
                type = "profile.updated.v1",
                tenantid = tenantId
            }));
        await transaction.CommitAsync();
    }

    private static async Task<bool> IsPublishedAsync(NpgsqlConnection connection, Guid eventId,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(
            "SELECT published_at IS NOT NULL FROM outbox WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", eventId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<Guid> EventIds { get; } = [];
        public bool FailAfterRecording { get; set; }

        public Task PublishAsync(Guid eventId, string eventType, ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("profile.updated.v1", eventType);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(eventId.ToString("D"),
                document.RootElement.GetProperty("id").GetString());
            EventIds.Add(eventId);
            if (FailAfterRecording)
            {
                FailAfterRecording = false;
                throw new InvalidOperationException("Synthetic uncertain confirmation.");
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
