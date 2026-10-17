using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Excellore.Notifications.Infrastructure;
using Excellore.Platform;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Resend.Webhooks;
using Testcontainers.PostgreSql;

namespace Excellore.Notifications.Tests;

public sealed class ProviderWebhookTests
{
    [Fact]
    public async Task Signed_callback_is_idempotent_and_cannot_cross_tenant_boundary()
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
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO notifications_worker,notifications_app");

        var migratorString = ConnectionString(container, "notifications_migrator", migratorPassword);
        await using var migrator = new NpgsqlConnection(migratorString);
        await migrator.OpenAsync();
        foreach (var migration in new[] { "001_initial.sql", "002_provider_webhooks.sql" })
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, migration)));

        var workerString = ConnectionString(container, "notifications_worker", workerPassword);
        var appString = ConnectionString(container, "notifications_app", appPassword);
        await using var workerSource = NpgsqlDataSource.Create(workerString);
        var database = new NotificationDatabase(workerSource, new PostgresEventInbox());
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var otherDeliveryId = Guid.NewGuid();
        await database.EnqueueOnceAsync(deliveryId, tenant, Guid.NewGuid(),
            "access-assignment-granted-v1", CancellationToken.None);
        await database.EnqueueOnceAsync(otherDeliveryId, otherTenant, Guid.NewGuid(),
            "access-assignment-granted-v1", CancellationToken.None);
        var delivery = await database.TryClaimAsync(tenant, deliveryId, CancellationToken.None);
        var other = await database.TryClaimAsync(otherTenant, otherDeliveryId, CancellationToken.None);
        Assert.NotNull(delivery);
        Assert.NotNull(other);

        var secret = $"whsec_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
        Environment.SetEnvironmentVariable("ConnectionStrings__Notifications", appString);
        Environment.SetEnvironmentVariable("ProviderWebhooks__ResendEnabled", "true");
        Environment.SetEnvironmentVariable("ProviderWebhooks__ResendSigningSecret", secret);
        Environment.SetEnvironmentVariable("ProviderWebhooks__MessageIdDomain", "notify.example.test");
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using (var invalid = BuildRequest("invalid", "{}", DateTimeOffset.UtcNow, "invalid"))
        {
            var response = await client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var providerEmailId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            type = "email.delivered",
            created_at = now,
            data = new
            {
                created_at = now,
                email_id = providerEmailId,
                message_id = $"<excellore-notification-{delivery!.ProviderCorrelationId:N}@notify.example.test>",
                from = "Excellore <notifications@example.test>",
                to = new[] { "member@example.test" },
                subject = "Access updated"
            }
        });
        var eventId = $"msg_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow;
        var signature = new WebhookSigner(secret).Sign(eventId, timestamp, payload);
        using (var valid = BuildRequest(eventId, payload, timestamp, signature))
        {
            var response = await client.SendAsync(valid);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        using (var duplicate = BuildRequest(eventId, payload, timestamp, signature))
        {
            var response = await client.SendAsync(duplicate);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var unknownPayload = payload.Replace(delivery.ProviderCorrelationId.ToString("N"),
            Guid.NewGuid().ToString("N"), StringComparison.Ordinal);
        var unknownId = $"msg_{Guid.NewGuid():N}";
        var unknownTimestamp = DateTimeOffset.UtcNow;
        var unknownSignature = new WebhookSigner(secret).Sign(
            unknownId, unknownTimestamp, unknownPayload);
        using (var unknown = BuildRequest(
                   unknownId, unknownPayload, unknownTimestamp, unknownSignature))
        {
            var response = await client.SendAsync(unknown);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var delayedPayload = payload.Replace(
            "email.delivered", "email.delivery_delayed", StringComparison.Ordinal);
        var delayedId = $"msg_{Guid.NewGuid():N}";
        var delayedTimestamp = DateTimeOffset.UtcNow;
        var delayedSignature = new WebhookSigner(secret).Sign(
            delayedId, delayedTimestamp, delayedPayload);
        using (var delayed = BuildRequest(
                   delayedId, delayedPayload, delayedTimestamp, delayedSignature))
        {
            var response = await client.SendAsync(delayed);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        await using var appSource = NpgsqlDataSource.Create(appString);
        Assert.Equal("delivered", await ReadProviderStatusAsync(appSource, tenant, deliveryId));
        Assert.Null(await ReadProviderStatusAsync(appSource, otherTenant, otherDeliveryId));
        Assert.Equal(3L, await CountReceiptsAsync(appSource));

        await using (var immutable = workerSource.CreateCommand("""
            UPDATE notification_provider_index SET tenant_id=@tenant
            WHERE correlation_id=@correlation
            """))
        {
            immutable.Parameters.AddWithValue("tenant", tenant);
            immutable.Parameters.AddWithValue("correlation", other!.ProviderCorrelationId);
            var denied = await Assert.ThrowsAsync<PostgresException>(
                () => immutable.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        Environment.SetEnvironmentVariable("ConnectionStrings__Notifications", null);
        Environment.SetEnvironmentVariable("ProviderWebhooks__ResendEnabled", null);
        Environment.SetEnvironmentVariable("ProviderWebhooks__ResendSigningSecret", null);
        Environment.SetEnvironmentVariable("ProviderWebhooks__MessageIdDomain", null);
        await using (var corrupt = new NpgsqlCommand("""
            UPDATE notification_provider_index SET tenant_id=@wrong
            WHERE correlation_id=@correlation
            """, migrator))
        {
            corrupt.Parameters.AddWithValue("wrong", tenant);
            corrupt.Parameters.AddWithValue("correlation", other.ProviderCorrelationId);
            await corrupt.ExecuteNonQueryAsync();
        }
        var crossPayload = payload.Replace(delivery.ProviderCorrelationId.ToString("N"),
            other.ProviderCorrelationId.ToString("N"), StringComparison.Ordinal);
        var crossId = $"msg_{Guid.NewGuid():N}";
        var crossTimestamp = DateTimeOffset.UtcNow;
        var crossSignature = new WebhookSigner(secret).Sign(crossId, crossTimestamp, crossPayload);
        using (var crossTenant = BuildRequest(crossId, crossPayload, crossTimestamp, crossSignature))
        {
            var response = await client.SendAsync(crossTenant);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        Assert.Null(await ReadProviderStatusAsync(appSource, otherTenant, otherDeliveryId));
    }

    private static HttpRequestMessage BuildRequest(string eventId, string payload,
        DateTimeOffset timestamp, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/v1/resend")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("svix-id", eventId);
        request.Headers.TryAddWithoutValidation("svix-timestamp",
            timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("svix-signature", signature);
        return request;
    }

    private static async Task<string?> ReadProviderStatusAsync(NpgsqlDataSource source,
        Guid tenantId, Guid deliveryId)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, tenantId);
        await using var command = new NpgsqlCommand(
            "SELECT provider_status FROM notification_deliveries WHERE id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", deliveryId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<long> CountReceiptsAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("SELECT count(*) FROM provider_webhook_receipts");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string ConnectionString(PostgreSqlContainer container, string username,
        string password) => new NpgsqlConnectionStringBuilder(container.GetConnectionString())
    {
        Username = username,
        Password = password
    }.ConnectionString;
}
