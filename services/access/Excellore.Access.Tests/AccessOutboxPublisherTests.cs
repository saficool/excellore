using System.Security.Cryptography;
using Excellore.Access.Application;
using Excellore.Access.Infrastructure;
using Excellore.Contracts;
using Excellore.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using AccessPublisherWorker = Excellore.Access.Worker.Worker;

namespace Excellore.Access.Tests;

public sealed class AccessOutboxPublisherTests
{
    [Fact]
    public async Task Restricted_worker_retries_uncertain_confirmation_and_publishes_authz_changes()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var publisherPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"CREATE ROLE access_migrator LOGIN PASSWORD '{migratorPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, $"CREATE ROLE access_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, $"CREATE ROLE access_publisher LOGIN PASSWORD '{publisherPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE, CREATE ON SCHEMA public TO access_migrator");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO access_app, access_publisher");
        var migratorString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_migrator", Password = migratorPassword
        }.ConnectionString;
        await using var migrator = new NpgsqlConnection(migratorString);
        await migrator.OpenAsync();
        foreach (var migration in new[]
        {
            "001_initial.sql", "002_register_apps.sql", "003_membership_index.sql", "004_outbox_publisher.sql"
        })
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, migration)));
        await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "004_outbox_publisher.sql")));

        var publisherString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_publisher", Password = publisherPassword
        }.ConnectionString;
        await using var publisherDb = NpgsqlDataSource.Create(publisherString);
        await using (var restricted = await publisherDb.OpenConnectionAsync())
        {
            Assert.True(await CheckAsync(restricted,
                "SELECT has_table_privilege(current_user, 'outbox', 'SELECT')"));
            Assert.True(await CheckAsync(restricted,
                "SELECT has_column_privilege(current_user, 'outbox', 'published_at', 'UPDATE')"));
            Assert.False(await CheckAsync(restricted,
                "SELECT has_column_privilege(current_user, 'outbox', 'payload', 'UPDATE')"));
            Assert.False(await CheckAsync(restricted,
                "SELECT has_table_privilege(current_user, 'outbox', 'INSERT')"));
            Assert.False(await CheckAsync(restricted,
                "SELECT has_table_privilege(current_user, 'tenant_members', 'SELECT')"));
            Assert.False(await CheckAsync(restricted,
                "SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user"));
        }

        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();
        await using (var command = new NpgsqlCommand("""
            INSERT INTO tenants (id, slug, name, status) VALUES
              (@one, 'publisher-one', 'One', 'active'),
              (@two, 'publisher-two', 'Two', 'active')
            """, admin))
        {
            command.Parameters.AddWithValue("one", tenantOne);
            command.Parameters.AddWithValue("two", tenantTwo);
            await command.ExecuteNonQueryAsync();
        }
        var appString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_app", Password = appPassword
        }.ConnectionString;
        await using var appDb = NpgsqlDataSource.Create(appString);
        Assert.Equal(CatalogSyncResult.Updated,
            await new CatalogSyncService(appDb).SyncAsync("access", new PermissionCatalog("access", [
                new CatalogPermission("access.app.access", "Open Access", false, false, false, ["owner"])
            ]), Guid.NewGuid()));
        Assert.Equal(2L, await CountAsync(admin, "outbox WHERE published_at IS NULL"));

        var bus = new FailingOnceBus();
        using var worker = new AccessPublisherWorker(publisherDb, bus, new PostgresOutboxDispatcher(),
            NullLogger<AccessPublisherWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await bus.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2L, await CountAsync(admin, "outbox WHERE published_at IS NULL"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (await CountAsync(admin, "outbox WHERE published_at IS NOT NULL") < 2)
                await Task.Delay(100, timeout.Token);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2L, await CountAsync(admin, "outbox WHERE published_at IS NOT NULL"));
        Assert.Equal(3, bus.EventIds.Count);
        Assert.Equal(bus.EventIds[0], bus.EventIds[1]);
        Assert.Equal(2, bus.EventIds.Distinct().Count());
        Assert.True(new HashSet<Guid> { tenantOne, tenantTwo }.SetEquals(bus.TenantIds));
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string condition)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {condition}", connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<bool> CheckAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FailingOnceBus : IEventBus
    {
        private int _failNext = 1;
        public TaskCompletionSource FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Guid> EventIds { get; } = [];
        public List<Guid> TenantIds { get; } = [];

        public Task<IEventPublisher> CreatePublisherAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IEventPublisher>(new Publisher(this));

        public Task<IAsyncDisposable> SubscribeAsync(string serviceName,
            IReadOnlyDictionary<string, PlatformEventHandler> handlers,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private sealed class Publisher(FailingOnceBus bus) : IEventPublisher
        {
            public Task PublishAsync(Guid eventId, string eventType, ReadOnlyMemory<byte> body,
                CancellationToken cancellationToken = default)
            {
                Assert.Equal(AccessAuthzChangedV1.EventType, eventType);
                var envelope = AccessAuthzChangedV1.Parse(body.Span);
                Assert.Equal(eventId, envelope.Id);
                bus.EventIds.Add(eventId);
                bus.TenantIds.Add(envelope.TenantId!.Value);
                if (Interlocked.Exchange(ref bus._failNext, 0) == 1)
                {
                    bus.FirstFailure.TrySetResult();
                    throw new InvalidOperationException("Synthetic uncertain confirmation");
                }
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
