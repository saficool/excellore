using Npgsql;

namespace Excellore.Notifications.Infrastructure;

public sealed record ProviderDeliveryUpdate(
    string Provider,
    string EventId,
    Guid CorrelationId,
    Guid ProviderEmailId,
    string Status,
    DateTimeOffset OccurredAt);

public enum ProviderWebhookResult
{
    Updated,
    Duplicate,
    UnknownCorrelation,
    IgnoredTransition
}

public sealed class ProviderWebhookDatabase(NpgsqlDataSource dataSource)
{
    private static readonly HashSet<string> AllowedStatuses =
        ["sent", "delivered", "delayed", "bounced", "complained", "failed", "suppressed"];

    public async Task<ProviderWebhookResult> ApplyAsync(ProviderDeliveryUpdate update,
        CancellationToken cancellationToken)
    {
        if (update.Provider != "resend" || update.EventId.Length is < 3 or > 160 ||
            update.EventId.Any(character => !(char.IsAsciiLetterOrDigit(character) ||
                                              character is '.' or '_' or '-')) ||
            update.CorrelationId == Guid.Empty || update.ProviderEmailId == Guid.Empty ||
            !AllowedStatuses.Contains(update.Status))
            throw new InvalidOperationException("Provider delivery update is invalid.");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var receipt = new NpgsqlCommand("""
            INSERT INTO provider_webhook_receipts (provider,event_id)
            VALUES (@provider,@event)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            receipt.Parameters.AddWithValue("provider", update.Provider);
            receipt.Parameters.AddWithValue("event", update.EventId);
            if (await receipt.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return ProviderWebhookResult.Duplicate;
            }
        }

        Guid? tenantId = null;
        Guid? deliveryId = null;
        await using (var lookup = new NpgsqlCommand("""
            SELECT tenant_id,delivery_id FROM notification_provider_index
            WHERE correlation_id=@correlation
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("correlation", update.CorrelationId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                tenantId = reader.GetGuid(0);
                deliveryId = reader.GetGuid(1);
            }
        }
        if (tenantId is null || deliveryId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ProviderWebhookResult.UnknownCorrelation;
        }

        await using (var tenant = new NpgsqlCommand(
                         "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction))
        {
            tenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString("D"));
            await tenant.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand("""
            UPDATE notification_deliveries
               SET provider_status=@status,
                   provider_email_id=@email,
                   provider_updated_at=@occurred
             WHERE id=@delivery AND tenant_id=@tenant
               AND provider_correlation_id=@correlation
               AND (
                 provider_status IS NULL OR
                 provider_status='sent' AND @status IN
                   ('delivered','delayed','bounced','complained','failed','suppressed') OR
                 provider_status='delayed' AND @status IN
                   ('delivered','bounced','complained','failed','suppressed') OR
                 provider_status='delivered' AND @status='complained'
               )
            """, connection, transaction);
        command.Parameters.AddWithValue("status", update.Status);
        command.Parameters.AddWithValue("email", update.ProviderEmailId);
        command.Parameters.AddWithValue("occurred", update.OccurredAt.ToUniversalTime());
        command.Parameters.AddWithValue("delivery", deliveryId.Value);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("correlation", update.CorrelationId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed == 1
            ? ProviderWebhookResult.Updated
            : ProviderWebhookResult.IgnoredTransition;
    }
}
