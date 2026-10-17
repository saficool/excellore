using MimeKit;

namespace Excellore.Notifications;

public sealed record EmailMessage(
    string RecipientAddress,
    string Subject,
    string TextBody,
    string? HtmlBody = null,
    string? ReplyToAddress = null,
    string? MessageId = null)
{
    public void Validate()
    {
        ValidateAddress(RecipientAddress, nameof(RecipientAddress));
        if (ReplyToAddress is not null) ValidateAddress(ReplyToAddress, nameof(ReplyToAddress));
        if (string.IsNullOrWhiteSpace(Subject) || Subject.Length > 200 ||
            Subject.Contains('\r') || Subject.Contains('\n'))
            throw new InvalidOperationException("Email subject is invalid.");
        if (string.IsNullOrWhiteSpace(TextBody) || TextBody.Length > 256_000)
            throw new InvalidOperationException("Email text body is invalid.");
        if (HtmlBody?.Length > 512_000)
            throw new InvalidOperationException("Email HTML body is invalid.");
        if (MessageId is not null &&
            (MessageId.Length > 254 || MessageId.Contains('\r') || MessageId.Contains('\n') ||
             MessageId.Any(char.IsWhiteSpace) || MessageId.Count(character => character == '@') != 1))
            throw new InvalidOperationException("Email Message-ID is invalid.");
    }

    internal static MailboxAddress ValidateAddress(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 ||
            !MailboxAddress.TryParse(value, out var parsed) ||
            !string.Equals(parsed.Address, value, StringComparison.Ordinal))
            throw new InvalidOperationException($"{parameterName} is invalid.");
        return parsed;
    }
}
