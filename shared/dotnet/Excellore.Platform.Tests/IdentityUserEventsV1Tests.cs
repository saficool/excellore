using System.Text.Json;
using Excellore.Contracts;

namespace Excellore.Platform.Tests;

public sealed class IdentityUserEventsV1Tests
{
    private static readonly string[] Types =
    [
        IdentityUserCreatedV1.EventType,
        IdentityUserUpdatedV1.EventType,
        IdentityUserDisabledV1.EventType,
        IdentityUserEnabledV1.EventType,
        IdentityUserDeletedV1.EventType
    ];

    [Fact]
    public void All_identity_events_parse_without_tenant_or_personal_values()
    {
        foreach (var type in Types)
        {
            var userId = Guid.NewGuid();
            var parsed = Parse(type, EventJson(type, new Dictionary<string, object>
            {
                ["userId"] = userId,
                ["changed"] = new[] { "email", "locale" },
                ["futureField"] = true
            }));
            Assert.Equal(userId, parsed);
        }
    }

    [Fact]
    public void All_identity_events_reject_tenant_and_personal_values()
    {
        foreach (var type in Types)
        {
            var data = new Dictionary<string, object> { ["userId"] = Guid.NewGuid() };
            if (type == IdentityUserUpdatedV1.EventType) data["changed"] = new[] { "email" };
            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, EventJson(type, data, Guid.NewGuid())));

            foreach (var forbidden in new[] { "email", "emailVerified", "givenName", "familyName", "locale" })
            {
                data[forbidden] = "personal value";
                Assert.Throws<InvalidPlatformEventException>(() => Parse(type, EventJson(type, data)));
                data.Remove(forbidden);
            }

            data["userId"] = Guid.Empty;
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type, EventJson(type, data)));
        }
    }

    [Fact]
    public void Updated_event_accepts_field_names_only()
    {
        var data = new Dictionary<string, object> { ["userId"] = Guid.NewGuid() };
        foreach (var changed in new[]
        {
            Array.Empty<string>(), new[] { "email", "email" }, new[] { "email@example.test" }
        })
        {
            data["changed"] = changed;
            Assert.Throws<InvalidPlatformEventException>(() =>
                IdentityUserUpdatedV1.Parse(EventJson(IdentityUserUpdatedV1.EventType, data)));
        }

        data.Remove("changed");
        Assert.Throws<InvalidPlatformEventException>(() =>
            IdentityUserUpdatedV1.Parse(EventJson(IdentityUserUpdatedV1.EventType, data)));
    }

    [Fact]
    public void Global_envelope_omits_tenant_id_when_serialized()
    {
        var original = new PlatformEvent<IdentityUserCreatedV1>
        {
            SpecVersion = "1.0",
            Id = Guid.NewGuid(),
            Type = IdentityUserCreatedV1.EventType,
            Source = "excellore/access",
            Time = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
            Actor = new EventActor { Sub = Guid.NewGuid().ToString("D"), Type = "machine" },
            Data = new IdentityUserCreatedV1 { UserId = Guid.NewGuid() }
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("tenantid", out _));
        Assert.Null(IdentityUserCreatedV1.Parse(json).TenantId);
    }

    private static Guid Parse(string type, byte[] json) => type switch
    {
        IdentityUserCreatedV1.EventType => IdentityUserCreatedV1.Parse(json).Data!.UserId,
        IdentityUserUpdatedV1.EventType => IdentityUserUpdatedV1.Parse(json).Data!.UserId,
        IdentityUserDisabledV1.EventType => IdentityUserDisabledV1.Parse(json).Data!.UserId,
        IdentityUserEnabledV1.EventType => IdentityUserEnabledV1.Parse(json).Data!.UserId,
        IdentityUserDeletedV1.EventType => IdentityUserDeletedV1.Parse(json).Data!.UserId,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    internal static byte[] EventJson(string type, Dictionary<string, object> data, Guid? tenantId = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid(),
            ["type"] = type,
            ["source"] = "excellore/access",
            ["time"] = DateTimeOffset.UtcNow,
            ["correlationid"] = Guid.NewGuid(),
            ["actor"] = new { sub = Guid.NewGuid().ToString("D"), type = "machine" },
            ["data"] = data
        };
        if (tenantId is not null) envelope["tenantid"] = tenantId;
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }
}
