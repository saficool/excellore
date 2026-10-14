using Excellore.Contracts;

namespace Excellore.Platform.Tests;

public sealed class AccessAssignmentEventsV1Tests
{
    private static readonly string[] Types =
    [
        AccessAssignmentGrantedV1.EventType,
        AccessAssignmentRevokedV1.EventType
    ];

    [Fact]
    public void Each_assignment_event_requires_matching_tenant_and_nonempty_user_and_role_ids()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in Types)
        {
            var data = Data(tenant);
            var parsed = Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, tenant, data));
            Assert.Equal(tenant, parsed.TenantId);

            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                TenantLifecycleEventsV1Tests.EventJson(type, Guid.NewGuid(), data)));
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                TenantLifecycleEventsV1Tests.EventJson(type, null, data)));

            foreach (var key in new[] { "userId", "roleId" })
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
    public void Each_assignment_event_accepts_optional_expiry_and_rejects_explicit_null()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in Types)
        {
            var data = Data(tenant);
            var expiry = DateTimeOffset.Parse("2026-12-01T12:00:00+00:00");
            data["expiresAt"] = expiry;
            Assert.Equal(expiry, Parse(type,
                TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)).ExpiresAt);

            data["expiresAt"] = null!;
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));

            data["expiresAt"] = "2026-12-01T12:00:00";
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));
        }
    }

    [Fact]
    public void Assignment_events_reject_personal_and_permission_values()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in Types)
        {
            var data = Data(tenant);
            foreach (var forbidden in new[]
            {
                "name", "description", "roleName", "email", "reason", "permissions"
            })
            {
                data[forbidden] = "custom value";
                Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                    TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));
                data.Remove(forbidden);
            }
        }
    }

    internal static Dictionary<string, object> Data(Guid tenantId) => new()
    {
        ["tenantId"] = tenantId,
        ["userId"] = Guid.NewGuid(),
        ["roleId"] = Guid.NewGuid()
    };

    internal static (Guid Id, Guid TenantId, DateTimeOffset? ExpiresAt) Parse(string type,
        byte[] json) => type switch
    {
        AccessAssignmentGrantedV1.EventType => From(AccessAssignmentGrantedV1.Parse(json)),
        AccessAssignmentRevokedV1.EventType => From(AccessAssignmentRevokedV1.Parse(json)),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static (Guid Id, Guid TenantId, DateTimeOffset? ExpiresAt) From(
        PlatformEvent<AccessAssignmentGrantedV1> envelope) =>
        (envelope.Id, envelope.TenantId!.Value, envelope.Data!.ExpiresAt);

    private static (Guid Id, Guid TenantId, DateTimeOffset? ExpiresAt) From(
        PlatformEvent<AccessAssignmentRevokedV1> envelope) =>
        (envelope.Id, envelope.TenantId!.Value, envelope.Data!.ExpiresAt);
}
