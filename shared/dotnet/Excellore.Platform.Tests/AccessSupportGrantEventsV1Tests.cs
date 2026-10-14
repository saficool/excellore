using Excellore.Contracts;

namespace Excellore.Platform.Tests;

public sealed class AccessSupportGrantEventsV1Tests
{
    private static readonly string[] Types =
    [
        AccessSupportGrantCreatedV1.EventType,
        AccessSupportGrantRevokedV1.EventType
    ];

    [Fact]
    public void Each_support_grant_event_requires_matching_tenant_and_nonempty_ids()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in Types)
        {
            var data = Data(tenant);
            Assert.Equal(tenant, Parse(type,
                TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)).TenantId);
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                TenantLifecycleEventsV1Tests.EventJson(type, Guid.NewGuid(), data)));
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                TenantLifecycleEventsV1Tests.EventJson(type, null, data)));

            foreach (var key in new[] { "grantId", "staffUserId" })
            {
                var original = data[key];
                data[key] = Guid.Empty;
                Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                    TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));
                data[key] = original;
            }
        }
    }

    [Fact]
    public void Support_grant_expiry_is_required_and_must_have_an_offset()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in Types)
        {
            var data = Data(tenant);
            data.Remove("expiresAt");
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));

            data["expiresAt"] = null!;
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));

            data["expiresAt"] = "2026-01-01T13:00:00";
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));
        }
    }

    [Fact]
    public void Creation_requires_future_expiry_but_revocation_accepts_elapsed_expiry()
    {
        var tenant = Guid.NewGuid();
        var data = Data(tenant);
        data["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.Throws<InvalidPlatformEventException>(() => Parse(
            AccessSupportGrantCreatedV1.EventType,
            TenantLifecycleEventsV1Tests.EventJson(AccessSupportGrantCreatedV1.EventType,
                tenant, data)));
        Assert.Equal(tenant, Parse(AccessSupportGrantRevokedV1.EventType,
            TenantLifecycleEventsV1Tests.EventJson(AccessSupportGrantRevokedV1.EventType,
                tenant, data)).TenantId);
    }

    [Fact]
    public void Support_grant_events_reject_personal_and_permission_values()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in Types)
        {
            var data = Data(tenant);
            foreach (var forbidden in new[]
            {
                "name", "description", "staffName", "email", "reason", "permissions", "roleName"
            })
            {
                data[forbidden] = "personal value";
                Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                    TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));
                data.Remove(forbidden);
            }
        }
    }

    internal static Dictionary<string, object> Data(Guid tenantId) => new()
    {
        ["tenantId"] = tenantId,
        ["grantId"] = Guid.NewGuid(),
        ["staffUserId"] = Guid.NewGuid(),
        ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1)
    };

    internal static (Guid Id, Guid TenantId) Parse(string type, byte[] json) => type switch
    {
        AccessSupportGrantCreatedV1.EventType => From(AccessSupportGrantCreatedV1.Parse(json)),
        AccessSupportGrantRevokedV1.EventType => From(AccessSupportGrantRevokedV1.Parse(json)),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static (Guid Id, Guid TenantId) From<TData>(PlatformEvent<TData> envelope)
        where TData : class => (envelope.Id, envelope.TenantId!.Value);
}
