namespace Excellore.Notifications.Application;

public sealed record NotificationContact(string EmailAddress);

public interface INotificationContactResolver
{
    Task<NotificationContact?> ResolveAsync(Guid tenantId, Guid userId,
        CancellationToken cancellationToken = default);
}

public interface INotificationTemplateRenderer
{
    EmailMessage Render(string templateKey, NotificationContact contact, Guid notificationId);
}
