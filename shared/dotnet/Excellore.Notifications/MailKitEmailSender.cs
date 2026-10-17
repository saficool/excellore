using MailKit.Security;
using MimeKit;

namespace Excellore.Notifications;

public sealed class MailKitEmailSender(SmtpEmailOptions options, bool allowInsecureLocal = false)
    : IEmailSender
{
    public async Task SendAsync(EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        options.Validate(allowInsecureLocal);
        message.Validate();

        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(options.FromName,
            EmailMessage.ValidateAddress(options.FromAddress!, nameof(options.FromAddress)).Address));
        mimeMessage.To.Add(EmailMessage.ValidateAddress(message.RecipientAddress,
            nameof(message.RecipientAddress)));
        if (message.ReplyToAddress is not null)
            mimeMessage.ReplyTo.Add(EmailMessage.ValidateAddress(message.ReplyToAddress,
                nameof(message.ReplyToAddress)));
        if (message.MessageId is not null) mimeMessage.MessageId = message.MessageId;
        mimeMessage.Subject = message.Subject;
        mimeMessage.Body = new BodyBuilder
        {
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody
        }.ToMessageBody();

        using var client = new MailKit.Net.Smtp.SmtpClient
        {
            Timeout = checked(options.TimeoutSeconds * 1000),
            CheckCertificateRevocation = true
        };
        await client.ConnectAsync(options.Host!, options.Port, ToSocketOption(options.Security),
            cancellationToken);
        if (options.Username is not null)
            await client.AuthenticateAsync(options.Username, options.Password!, cancellationToken);
        await client.SendAsync(mimeMessage, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static SecureSocketOptions ToSocketOption(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.TlsOnConnect => SecureSocketOptions.SslOnConnect,
        _ => throw new InvalidOperationException("SMTP security mode is invalid.")
    };
}
