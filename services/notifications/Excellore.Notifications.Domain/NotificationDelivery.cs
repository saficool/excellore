namespace Excellore.Notifications.Domain;

public static class NotificationTemplates
{
    public const string AccessAssignmentGranted = "access-assignment-granted-v1";
}

public sealed record PendingNotification(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    string TemplateKey,
    int Attempt,
    Guid ProviderCorrelationId);
