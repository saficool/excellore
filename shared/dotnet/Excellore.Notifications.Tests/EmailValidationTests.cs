using Excellore.Notifications;

namespace Excellore.Notifications.Tests;

public sealed class EmailValidationTests
{
    [Fact]
    public void Production_transport_requires_tls()
    {
        var options = ValidOptions() with { Security = SmtpSecurity.None };

        var error = Assert.Throws<InvalidOperationException>(() => options.Validate(false));

        Assert.Equal("SMTP TLS is required.", error.Message);
    }

    [Fact]
    public void Local_transport_may_use_mailpit_without_authentication_or_tls()
    {
        var options = ValidOptions() with
        {
            Host = "127.0.0.1",
            Port = 1025,
            Username = null,
            Password = null,
            Security = SmtpSecurity.None
        };

        options.Validate(true);
    }

    [Fact]
    public void Credentials_must_be_supplied_as_a_pair()
    {
        var options = ValidOptions() with { Password = null };

        var error = Assert.Throws<InvalidOperationException>(() => options.Validate(false));

        Assert.Equal("SMTP credentials are incomplete.", error.Message);
    }

    [Theory]
    [InlineData("victim@example.test\r\nBcc: attacker@example.test", "Subject")]
    [InlineData("victim@example.test", "Subject\r\nBcc: attacker@example.test")]
    public void Message_rejects_header_injection(string recipient, string subject)
    {
        var message = new EmailMessage(recipient, subject, "Safe body");

        Assert.Throws<InvalidOperationException>(message.Validate);
    }

    private static SmtpEmailOptions ValidOptions() => new(
        "smtp.example.test", 587, "sender", "secret", "notify@example.test",
        "Excellore", SmtpSecurity.StartTls);
}
