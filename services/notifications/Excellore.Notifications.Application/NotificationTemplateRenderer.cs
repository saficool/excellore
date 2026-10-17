using System.Text.Encodings.Web;
using Excellore.Notifications.Domain;

namespace Excellore.Notifications.Application;

public sealed class NotificationTemplateRenderer
    : INotificationTemplateRenderer
{
    private readonly Uri _shellOrigin;

    public NotificationTemplateRenderer(string? shellOrigin, bool allowLocalHttp)
    {
        if (!Uri.TryCreate(shellOrigin, UriKind.Absolute, out var origin) ||
            (origin.Scheme != Uri.UriSchemeHttps &&
             !(allowLocalHttp && origin.Scheme == Uri.UriSchemeHttp &&
               (origin.IsLoopback || origin.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))) ||
            !string.IsNullOrEmpty(origin.UserInfo) || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 || origin.AbsolutePath != "/")
            throw new InvalidOperationException("Notification shell origin is not configured.");
        _shellOrigin = origin;
    }

    public EmailMessage Render(string templateKey, NotificationContact contact,
        Guid notificationId)
    {
        if (templateKey != NotificationTemplates.AccessAssignmentGranted)
            throw new InvalidOperationException("Notification template is not allowed.");

        var encodedOrigin = HtmlEncoder.Default.Encode(_shellOrigin.AbsoluteUri);
        return new EmailMessage(
            contact.EmailAddress,
            "Your Excellore access was updated",
            $"Your access in Excellore was updated.\n\nSign in at {_shellOrigin}\n\n" +
            "If you did not expect this change, contact your organization administrator.",
            $"""
            <!doctype html>
            <html lang="en">
            <body style="font-family:Arial,sans-serif;color:#172033;line-height:1.5">
              <h1 style="font-size:24px">Your Excellore access was updated</h1>
              <p>An administrator updated your access in Excellore.</p>
              <p><a href="{encodedOrigin}">Open Excellore</a></p>
              <p>If you did not expect this change, contact your organization administrator.</p>
            </body>
            </html>
            """);
    }
}
