using System.Data;
using Excellore.Notifications.Domain;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Notifications.Infrastructure;

public sealed class NotificationDatabase(NpgsqlDataSource dataSource, PostgresEventInbox inbox)
{
    public async Task EnqueueOnceAsync(Guid eventId, Guid tenantId, Guid userId,
        string templateKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await inbox.RunOnceAsync(connection, eventId, tenantId, async (transaction, token) =>
        {
            var correlationId = Guid.NewGuid();
            await using var command = new NpgsqlCommand("""
                INSERT INTO notification_deliveries
                  (id,tenant_id,event_id,user_id,template_key,channel,status,provider_correlation_id)
                VALUES (@id,@tenant,@event,@user,@template,'email','pending',@correlation);
                INSERT INTO notification_provider_index (correlation_id,tenant_id,delivery_id)
                VALUES (@correlation,@tenant,@id)
                """, connection, (NpgsqlTransaction)transaction);
            command.Parameters.AddWithValue("id", eventId);
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("event", eventId);
            command.Parameters.AddWithValue("user", userId);
            command.Parameters.AddWithValue("template", templateKey);
            command.Parameters.AddWithValue("correlation", correlationId);
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }

    public async Task<PendingNotification?> TryClaimAsync(Guid tenantId, Guid notificationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE notification_deliveries
               SET status='sending', attempts=attempts+1,
                   lease_until=now()+interval '2 minutes', failure_code=NULL, updated_at=now()
             WHERE id=@id AND tenant_id=@tenant AND attempts < 3
               AND (status IN ('pending','failed') OR
                    (status='sending' AND lease_until < now()))
            RETURNING id,tenant_id,user_id,template_key,attempts,provider_correlation_id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", notificationId);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        PendingNotification? result = null;
        if (await reader.ReadAsync(cancellationToken))
            result = new PendingNotification(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetGuid(5));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task MarkSentAsync(Guid tenantId, Guid notificationId, CancellationToken token) =>
        UpdateStatusAsync(tenantId, notificationId, "sent", null, token);

    public Task MarkFailedAsync(Guid tenantId, Guid notificationId, CancellationToken token) =>
        UpdateStatusAsync(tenantId, notificationId, "failed", "delivery-unavailable", token);

    public Task MarkSuppressedAsync(Guid tenantId, Guid notificationId, CancellationToken token) =>
        UpdateStatusAsync(tenantId, notificationId, "suppressed", "contact-unavailable", token);

    private async Task UpdateStatusAsync(Guid tenantId, Guid notificationId, string status,
        string? failureCode, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE notification_deliveries
               SET status=@status, failure_code=@failure, lease_until=NULL,
                   sent_at=CASE WHEN @status='sent' THEN now() ELSE sent_at END, updated_at=now()
             WHERE id=@id AND tenant_id=@tenant AND status='sending'
            """, connection, transaction);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("failure", (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("id", notificationId);
        command.Parameters.AddWithValue("tenant", tenantId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Notification delivery state is unavailable.");
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new InvalidOperationException("Tenant context is required.");
        await using var command = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
