using Excellore.Notifications.Application;
using Excellore.Notifications.Domain;

namespace Excellore.Notifications.Tests;

public sealed class NotificationTemplateTests
{
    [Fact]
    public void Renderer_accepts_only_code_defined_template_and_contains_no_identifiers()
    {
        var renderer = new NotificationTemplateRenderer("https://excellore.example.test", false,
            "notify.example.test");
        var notificationId = Guid.NewGuid();
        var message = renderer.Render(NotificationTemplates.AccessAssignmentGranted,
            new NotificationContact("member@example.test"), notificationId);

        message.Validate();
        Assert.Equal("member@example.test", message.RecipientAddress);
        Assert.Equal($"excellore-notification-{notificationId:N}@notify.example.test", message.MessageId);
        Assert.DoesNotContain(notificationId.ToString(), message.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => renderer.Render("arbitrary-template",
            new NotificationContact("member@example.test"), notificationId));
    }
}
