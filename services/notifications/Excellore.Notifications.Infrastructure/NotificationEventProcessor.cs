using Excellore.Contracts;
using Excellore.Notifications.Application;
using Excellore.Notifications.Domain;

namespace Excellore.Notifications.Infrastructure;

public sealed class NotificationEventProcessor(NotificationDatabase database,
    INotificationContactResolver contacts, INotificationTemplateRenderer templates,
    IEmailSender emailSender)
{
    public async Task HandleAssignmentGrantedAsync(ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        var message = AccessAssignmentGrantedV1.Parse(body.Span);
        var data = message.Data!;
        await database.EnqueueOnceAsync(message.Id, data.TenantId, data.UserId,
            NotificationTemplates.AccessAssignmentGranted, cancellationToken);
        var delivery = await database.TryClaimAsync(data.TenantId, message.Id, cancellationToken);
        if (delivery is null) return;
        try
        {
            var contact = await contacts.ResolveAsync(delivery.TenantId, delivery.UserId,
                cancellationToken);
            if (contact is null)
            {
                await database.MarkSuppressedAsync(delivery.TenantId, delivery.Id, cancellationToken);
                return;
            }
            await emailSender.SendAsync(templates.Render(delivery.TemplateKey, contact,
                    delivery.ProviderCorrelationId),
                cancellationToken);
            await database.MarkSentAsync(delivery.TenantId, delivery.Id, cancellationToken);
        }
        catch
        {
            await database.MarkFailedAsync(delivery.TenantId, delivery.Id, cancellationToken);
            throw;
        }
    }
}
