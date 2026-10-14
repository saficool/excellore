using Excellore.Contracts;

namespace Excellore.Platform.Tests;

public sealed class TenantMemberEventsV1Tests
{
    private static readonly string[] Types =
    [
        TenantMemberAddedV1.EventType,
        TenantMemberSuspendedV1.EventType,
        TenantMemberReactivatedV1.EventType,
        TenantMemberRemovedV1.EventType
    ];

    [Fact]
    public void Each_member_event_requires_matching_tenant_and_nonempty_user()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in Types)
        {
            var data = Data(type, tenant);
            var parsed = Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, tenant, data));
            Assert.Equal(tenant, parsed.TenantId);

            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, Guid.NewGuid(), data)));
            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, null, data)));

            data["userId"] = Guid.Empty;
            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));
        }
    }

    [Fact]
    public void Member_events_reject_role_values()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in Types)
        {
            var data = Data(type, tenant);
            data["roles"] = new[] { "custom role" };
            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));
            data.Remove("roles");
            data["roleNames"] = new[] { "custom role" };
            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));
        }
    }

    [Fact]
    public void Added_event_requires_nonempty_membership_id()
    {
        var tenant = Guid.NewGuid();
        var data = Data(TenantMemberAddedV1.EventType, tenant);
        data["membershipId"] = Guid.Empty;
        Assert.Throws<InvalidPlatformEventException>(() => TenantMemberAddedV1.Parse(
            TenantLifecycleEventsV1Tests.EventJson(TenantMemberAddedV1.EventType, tenant, data)));
        data.Remove("membershipId");
        Assert.Throws<InvalidPlatformEventException>(() => TenantMemberAddedV1.Parse(
            TenantLifecycleEventsV1Tests.EventJson(TenantMemberAddedV1.EventType, tenant, data)));
    }

    internal static Dictionary<string, object> Data(string type, Guid tenantId)
    {
        var data = new Dictionary<string, object>
        {
            ["tenantId"] = tenantId,
            ["userId"] = Guid.NewGuid()
        };
        if (type == TenantMemberAddedV1.EventType) data["membershipId"] = Guid.NewGuid();
        return data;
    }

    internal static (Guid Id, Guid TenantId) Parse(string type, byte[] json) => type switch
    {
        TenantMemberAddedV1.EventType => From(TenantMemberAddedV1.Parse(json)),
        TenantMemberSuspendedV1.EventType => From(TenantMemberSuspendedV1.Parse(json)),
        TenantMemberReactivatedV1.EventType => From(TenantMemberReactivatedV1.Parse(json)),
        TenantMemberRemovedV1.EventType => From(TenantMemberRemovedV1.Parse(json)),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static (Guid Id, Guid TenantId) From<TData>(PlatformEvent<TData> envelope)
        where TData : class => (envelope.Id, envelope.TenantId!.Value);
}
