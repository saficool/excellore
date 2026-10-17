using Excellore.Notifications.Domain;

namespace Excellore.Notifications.Tests;

public sealed class NotificationMessageIdTests
{
    [Fact]
    public void Round_trip_contains_only_random_correlation_and_configured_domain()
    {
        var correlation = Guid.NewGuid();
        var messageId = NotificationMessageId.Create(correlation, "Notify.Example.Test");

        Assert.Equal($"excellore-notification-{correlation:N}@notify.example.test", messageId);
        Assert.True(NotificationMessageId.TryParse($"<{messageId}>", "notify.example.test",
            out var parsed));
        Assert.Equal(correlation, parsed);
    }

    [Theory]
    [InlineData("customer@example.test")]
    [InlineData("excellore-notification-not-a-guid@notify.example.test")]
    [InlineData("excellore-notification-00000000000000000000000000000000@notify.example.test")]
    public void Parser_rejects_foreign_or_invalid_values(string value)
    {
        Assert.False(NotificationMessageId.TryParse(value, "notify.example.test", out _));
    }

    [Theory]
    [InlineData("-notify.example.test")]
    [InlineData("notify..example.test")]
    [InlineData("localhost")]
    public void Creator_rejects_invalid_domains(string domain)
    {
        Assert.Throws<InvalidOperationException>(() =>
            NotificationMessageId.Create(Guid.NewGuid(), domain));
    }
}
