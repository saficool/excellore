using System.Security.Cryptography;
using System.Text.Json;
using Excellore.Contracts;
using Excellore.Notifications.Application;
using Excellore.Notifications.Infrastructure;
using Excellore.Platform;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Notifications.Tests;

public sealed class NotificationIsolationTests
{
    [Fact]
    public async Task Duplicate_event_sends_once_and_wrong_tenant_cannot_read_delivery()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var workerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin,
            $"CREATE ROLE notifications_migrator LOGIN PASSWORD '{migratorPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin,
            $"CREATE ROLE notifications_worker LOGIN PASSWORD '{workerPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin,
            $"CREATE ROLE notifications_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE,CREATE ON SCHEMA public TO notifications_migrator");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO notifications_worker");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO notifications_app");
        var migratorString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "notifications_migrator", Password = migratorPassword
        }.ConnectionString;
        await using (var migrator = new NpgsqlConnection(migratorString))
        {
            await migrator.OpenAsync();
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "001_initial.sql")));
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "002_provider_webhooks.sql")));
        }

        var workerString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "notifications_worker", Password = workerPassword
        }.ConnectionString;
        await using var source = NpgsqlDataSource.Create(workerString);
        var sender = new CaptureEmailSender();
        var processor = new NotificationEventProcessor(
            new NotificationDatabase(source, new PostgresEventInbox()),
            new StaticContactResolver(),
            new NotificationTemplateRenderer("https://excellore.example.test", false,
                "notify.example.test"), sender);
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var envelope = new PlatformEvent<AccessAssignmentGrantedV1>
        {
            SpecVersion = "1.0",
            Id = Guid.NewGuid(),
            Type = AccessAssignmentGrantedV1.EventType,
            Source = AccessAssignmentGrantedV1.EventSource,
            Time = DateTimeOffset.UtcNow,
            TenantId = tenant,
            CorrelationId = Guid.NewGuid(),
            Actor = new EventActor { Sub = Guid.NewGuid().ToString("D"), Type = "human" },
            Data = new AccessAssignmentGrantedV1
            {
                TenantId = tenant,
                UserId = Guid.NewGuid(),
                RoleId = Guid.NewGuid()
            }
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope);

        await processor.HandleAssignmentGrantedAsync(body, CancellationToken.None);
        await processor.HandleAssignmentGrantedAsync(body, CancellationToken.None);

        Assert.Single(sender.Messages);
        Assert.Equal(1L, await CountAsync(source, tenant));
        Assert.Equal(0L, await CountAsync(source, otherTenant));
    }

    private static async Task<long> CountAsync(NpgsqlDataSource source, Guid tenantId)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await set.ExecuteNonQueryAsync();
        }
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM notification_deliveries", connection, transaction);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class StaticContactResolver : INotificationContactResolver
    {
        public Task<NotificationContact?> ResolveAsync(Guid tenantId, Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<NotificationContact?>(new("member@example.test"));
    }

    private sealed class CaptureEmailSender : IEmailSender
    {
        public List<EmailMessage> Messages { get; } = [];
        public Task SendAsync(EmailMessage message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
