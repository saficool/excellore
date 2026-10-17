using System.Text.Encodings.Web;
using Excellore.Notifications;

namespace Excellore.Access.Infrastructure;

public interface IInvitationDelivery
{
    Task SendAsync(string email, Guid invitationId, string oneTimeCode,
        CancellationToken cancellationToken = default);
}

public sealed class SmtpInvitationDelivery(IEmailSender emailSender,
    string? acceptanceOrigin, bool allowLocalHttp) : IInvitationDelivery
{
    public static Uri BuildAcceptanceLink(Uri origin, Guid invitationId) =>
        new(origin, $"/invitations/accept?id={invitationId:D}");

    public async Task SendAsync(string email, Guid invitationId, string oneTimeCode,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(acceptanceOrigin, UriKind.Absolute, out var origin) ||
            (origin.Scheme != Uri.UriSchemeHttps &&
             !(allowLocalHttp && origin.Scheme == Uri.UriSchemeHttp &&
               (origin.IsLoopback || origin.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))) ||
            !string.IsNullOrEmpty(origin.UserInfo) || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 || origin.AbsolutePath != "/")
            throw new InvalidOperationException("Invitation delivery is not configured.");

        var link = BuildAcceptanceLink(origin, invitationId);
        var encodedLink = HtmlEncoder.Default.Encode(link.AbsoluteUri);
        var encodedCode = HtmlEncoder.Default.Encode(oneTimeCode);
        await emailSender.SendAsync(new EmailMessage(
            email,
            "Your Excellore invitation",
            $"You have been invited to Excellore.\n\nOpen {link} and sign in. " +
            $"Enter this one-time invitation code: {oneTimeCode}\n\n" +
            "The code expires in seven days. If you did not expect this invitation, ignore this email.",
            $"""
            <!doctype html>
            <html lang="en">
            <body style="font-family:Arial,sans-serif;color:#172033;line-height:1.5">
              <h1 style="font-size:24px">You have been invited to Excellore</h1>
              <p><a href="{encodedLink}">Open your invitation</a> and sign in.</p>
              <p>Enter this one-time invitation code:</p>
              <p style="font-size:22px;font-weight:700;letter-spacing:2px">{encodedCode}</p>
              <p>The code expires in seven days.</p>
              <p>If you did not expect this invitation, ignore this email.</p>
            </body>
            </html>
            """), cancellationToken);
    }
}
