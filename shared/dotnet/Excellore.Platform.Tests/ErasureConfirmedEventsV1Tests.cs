using System.Text.Json;
using Excellore.Contracts;

namespace Excellore.Platform.Tests;

public sealed class ErasureConfirmedEventsV1Tests
{
    private static readonly string[] Types =
    [
        AccessErasureConfirmedV1.EventType,
        ProfileErasureConfirmedV1.EventType
    ];

    [Fact]
    public void Each_service_accepts_tenant_and_global_user_confirmations()
    {
        foreach (var type in Types)
        {
            var tenantId = Guid.NewGuid();
            var tenantData = TenantData(type, tenantId);
            tenantData["futureField"] = true;
            var tenant = Parse(type, EventJson(type, tenantData, tenantId));
            Assert.Equal(tenantId, tenant.TenantId);
            Assert.Null(tenant.UserId);
            Assert.Equal("tenant", tenant.Scope);

            var userId = Guid.NewGuid();
            var userData = UserData(type, userId);
            var user = Parse(type, EventJson(type, userData));
            Assert.Null(user.TenantId);
            Assert.Equal(userId, user.UserId);
            Assert.Equal("user", user.Scope);
        }
    }

    [Fact]
    public void Tenant_confirmations_require_matching_nonempty_tenant_and_no_user_id()
    {
        foreach (var type in Types)
        {
            var tenantId = Guid.NewGuid();
            var data = TenantData(type, tenantId);
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                EventJson(type, data, Guid.NewGuid())));
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                EventJson(type, data)));

            data["tenantId"] = Guid.Empty;
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                EventJson(type, data, tenantId)));
            data["tenantId"] = tenantId;
            data["userId"] = null!;
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                EventJson(type, data, tenantId)));
        }
    }

    [Fact]
    public void User_confirmations_require_nonempty_user_and_no_tenant_id()
    {
        foreach (var type in Types)
        {
            var data = UserData(type, Guid.NewGuid());
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                EventJson(type, data, Guid.NewGuid())));

            data["userId"] = Guid.Empty;
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                EventJson(type, data)));
            data["userId"] = Guid.NewGuid();
            data["tenantId"] = null!;
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                EventJson(type, data)));
        }
    }

    [Fact]
    public void Service_code_and_source_must_match_event_type()
    {
        foreach (var type in Types)
        {
            var tenantId = Guid.NewGuid();
            var data = TenantData(type, tenantId);
            data["service"] = type == AccessErasureConfirmedV1.EventType ? "profile" : "access";
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                EventJson(type, data, tenantId)));

            data["service"] = Service(type);
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                EventJson(type, data, tenantId, "excellore/other")));
        }
    }

    [Fact]
    public void Confirmations_reject_personal_values_and_tokens()
    {
        foreach (var type in Types)
        {
            var tenantId = Guid.NewGuid();
            var data = TenantData(type, tenantId);
            foreach (var forbidden in new[]
            {
                "name", "email", "reason", "objectKey", "downloadUrl", "token", "details"
            })
            {
                data[forbidden] = "personal value";
                Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                    EventJson(type, data, tenantId)));
                data.Remove(forbidden);
            }
        }
    }

    [Fact]
    public void User_confirmation_serialization_omits_tenant_fields()
    {
        var original = new PlatformEvent<ProfileErasureConfirmedV1>
        {
            SpecVersion = "1.0",
            Id = Guid.NewGuid(),
            Type = ProfileErasureConfirmedV1.EventType,
            Source = ProfileErasureConfirmedV1.EventSource,
            Time = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
            Actor = new EventActor { Sub = Guid.NewGuid().ToString("D"), Type = "machine" },
            Data = new ProfileErasureConfirmedV1
            {
                Scope = "user", UserId = Guid.NewGuid(), Service = "profile"
            }
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("tenantid", out _));
        Assert.False(document.RootElement.GetProperty("data").TryGetProperty("tenantId", out _));
        Assert.Equal("user", ProfileErasureConfirmedV1.Parse(json).Data!.Scope);
    }

    internal static Dictionary<string, object> TenantData(string type, Guid tenantId) => new()
    {
        ["scope"] = "tenant",
        ["tenantId"] = tenantId,
        ["service"] = Service(type)
    };

    internal static Dictionary<string, object> UserData(string type, Guid userId) => new()
    {
        ["scope"] = "user",
        ["userId"] = userId,
        ["service"] = Service(type)
    };

    internal static (Guid Id, Guid? TenantId, Guid? UserId, string? Scope) Parse(
        string type, byte[] json) => type switch
    {
        AccessErasureConfirmedV1.EventType => From(AccessErasureConfirmedV1.Parse(json)),
        ProfileErasureConfirmedV1.EventType => From(ProfileErasureConfirmedV1.Parse(json)),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static (Guid Id, Guid? TenantId, Guid? UserId, string? Scope) From<TData>(
        PlatformEvent<TData> envelope) where TData : ErasureConfirmationDataV1 =>
        (envelope.Id, envelope.TenantId, envelope.Data!.UserId, envelope.Data.Scope);

    internal static byte[] EventJson(string type, Dictionary<string, object> data,
        Guid? tenantId = null, string? source = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid(),
            ["type"] = type,
            ["source"] = source ?? $"excellore/{Service(type)}",
            ["time"] = DateTimeOffset.UtcNow,
            ["correlationid"] = Guid.NewGuid(),
            ["actor"] = new { sub = Guid.NewGuid().ToString("D"), type = "machine" },
            ["data"] = data
        };
        if (tenantId is not null) envelope["tenantid"] = tenantId;
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }

    private static string Service(string type) => type switch
    {
        AccessErasureConfirmedV1.EventType => "access",
        ProfileErasureConfirmedV1.EventType => "profile",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
