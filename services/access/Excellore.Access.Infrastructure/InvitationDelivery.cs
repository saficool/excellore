using System.Net.Mail;

namespace Excellore.Access.Infrastructure;

public interface IInvitationDelivery
{
    Task SendAsync(string email, Guid invitationId, string oneTimeCode,
        CancellationToken cancellationToken = default);
}

public sealed class SmtpInvitationDelivery(string? host, int port, string? fromAddress,
    string? acceptanceOrigin, bool useTls, bool allowLocalHttp) : IInvitationDelivery
{
    public static Uri BuildAcceptanceLink(Uri origin, Guid invitationId) =>
        new(origin, $"/invitations/accept?id={invitationId:D}");

    public async Task SendAsync(string email, Guid invitationId, string oneTimeCode,
        CancellationToken cancellationToken = default)
    {
        if (host is not { Length: > 0 } || port is < 1 or > 65535 ||
            !MailAddress.TryCreate(fromAddress, out var from) ||
            !Uri.TryCreate(acceptanceOrigin, UriKind.Absolute, out var origin) ||
            (origin.Scheme != Uri.UriSchemeHttps &&
             !(allowLocalHttp && origin.Scheme == Uri.UriSchemeHttp &&
               (origin.IsLoopback || origin.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))) ||
            !string.IsNullOrEmpty(origin.UserInfo) || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 || origin.AbsolutePath != "/" ||
            (!useTls && !allowLocalHttp))
            throw new InvalidOperationException("Invitation delivery is not configured.");

        var link = BuildAcceptanceLink(origin, invitationId);
        using var message = new MailMessage(from, new MailAddress(email))
        {
            Subject = "Your Excellore invitation",
            Body = $"Open {link} and sign in. Enter this one-time invitation code: {oneTimeCode}\n\n" +
                   "The code expires in seven days. If you did not expect this invitation, ignore this email.",
            IsBodyHtml = false
        };
        using var client = new SmtpClient(host, port) { EnableSsl = useTls };
        await client.SendMailAsync(message, cancellationToken);
    }
}
