using Excellore.Contracts;

namespace Excellore.Platform.Tests;

public sealed class AccessRoleEventsV1Tests
{
    private static readonly string[] Types =
    [
        AccessRoleCreatedV1.EventType,
        AccessRoleUpdatedV1.EventType,
        AccessRoleDeletedV1.EventType
    ];

    [Fact]
    public void Each_role_event_requires_matching_tenant_and_nonempty_role_id()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in Types)
        {
            var data = Data(tenant);
            var parsed = Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, tenant, data));
            Assert.Equal(tenant, parsed.TenantId);

            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, Guid.NewGuid(), data)));
            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, null, data)));

            data["roleId"] = Guid.Empty;
            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));
        }
    }

    [Fact]
    public void Role_events_reject_names_descriptions_and_permission_lists()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in Types)
        {
            var data = Data(tenant);
            foreach (var forbidden in new[] { "name", "description", "permissions" })
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
        ["roleId"] = Guid.NewGuid()
    };

    internal static (Guid Id, Guid TenantId) Parse(string type, byte[] json) => type switch
    {
        AccessRoleCreatedV1.EventType => From(AccessRoleCreatedV1.Parse(json)),
        AccessRoleUpdatedV1.EventType => From(AccessRoleUpdatedV1.Parse(json)),
        AccessRoleDeletedV1.EventType => From(AccessRoleDeletedV1.Parse(json)),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static (Guid Id, Guid TenantId) From<TData>(PlatformEvent<TData> envelope)
        where TData : class => (envelope.Id, envelope.TenantId!.Value);
}
