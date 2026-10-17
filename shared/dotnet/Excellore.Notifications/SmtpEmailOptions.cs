namespace Excellore.Notifications;

public enum SmtpSecurity
{
    None,
    StartTls,
    TlsOnConnect
}

public sealed record SmtpEmailOptions(
    string? Host,
    int Port,
    string? Username,
    string? Password,
    string? FromAddress,
    string FromName,
    SmtpSecurity Security,
    int TimeoutSeconds = 15)
{
    public void Validate(bool allowInsecureLocal)
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 ||
            Host.Any(char.IsWhiteSpace) || Port is < 1 or > 65535 ||
            TimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("SMTP transport is not configured.");

        EmailMessage.ValidateAddress(FromAddress ?? string.Empty, nameof(FromAddress));
        if (string.IsNullOrWhiteSpace(FromName) || FromName.Length > 100 ||
            FromName.Contains('\r') || FromName.Contains('\n'))
            throw new InvalidOperationException("SMTP sender name is invalid.");
        if ((Username is null) != (Password is null) || Username is { Length: 0 } ||
            Password is { Length: 0 })
            throw new InvalidOperationException("SMTP credentials are incomplete.");
        if (!allowInsecureLocal && Security == SmtpSecurity.None)
            throw new InvalidOperationException("SMTP TLS is required.");
    }
}
