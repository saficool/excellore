using Excellore.Contracts;

namespace Excellore.Platform.Tests;

public sealed class TenantMetadataEventsV1Tests
{
    private static readonly string[] Types =
    [
        TenantCreatedV1.EventType,
        TenantUpdatedV1.EventType,
        TenantAppEnabledV1.EventType,
        TenantAppDisabledV1.EventType
    ];

    [Fact]
    public void Metadata_events_require_matching_tenant_ids_and_reject_values()
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

            foreach (var forbidden in new[]
            {
                "slug", "previousSlug", "name", "plan", "apps", "features"
            })
            {
                data[forbidden] = "identifying value";
                Assert.Throws<InvalidPlatformEventException>(() =>
                    Parse(type, TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));
                data.Remove(forbidden);
            }
        }
    }

    [Fact]
    public void Updated_event_carries_changed_field_names_only()
    {
        var tenant = Guid.NewGuid();
        var data = Data(TenantUpdatedV1.EventType, tenant);
        foreach (var changed in new[]
        {
            Array.Empty<string>(), new[] { "slug", "slug" }, new[] { "private-value" }
        })
        {
            data["changed"] = changed;
            Assert.Throws<InvalidPlatformEventException>(() => TenantUpdatedV1.Parse(
                TenantLifecycleEventsV1Tests.EventJson(TenantUpdatedV1.EventType, tenant, data)));
        }
        data.Remove("changed");
        Assert.Throws<InvalidPlatformEventException>(() => TenantUpdatedV1.Parse(
            TenantLifecycleEventsV1Tests.EventJson(TenantUpdatedV1.EventType, tenant, data)));
    }

    [Fact]
    public void App_events_require_bounded_machine_safe_app_id()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in new[] { TenantAppEnabledV1.EventType, TenantAppDisabledV1.EventType })
        {
            var data = Data(type, tenant);
            foreach (var appId in new[] { "", "My App", "email@example.test", new string('a', 65) })
            {
                data["appId"] = appId;
                Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                    TenantLifecycleEventsV1Tests.EventJson(type, tenant, data)));
            }
        }
    }

    internal static Dictionary<string, object> Data(string type, Guid tenantId)
    {
        var data = new Dictionary<string, object> { ["tenantId"] = tenantId };
        if (type == TenantUpdatedV1.EventType) data["changed"] = new[] { "slug" };
        if (type is TenantAppEnabledV1.EventType or TenantAppDisabledV1.EventType)
            data["appId"] = "profile";
        return data;
    }

    internal static (Guid Id, Guid TenantId) Parse(string type, byte[] json) => type switch
    {
        TenantCreatedV1.EventType => From(TenantCreatedV1.Parse(json)),
        TenantUpdatedV1.EventType => From(TenantUpdatedV1.Parse(json)),
        TenantAppEnabledV1.EventType => From(TenantAppEnabledV1.Parse(json)),
        TenantAppDisabledV1.EventType => From(TenantAppDisabledV1.Parse(json)),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static (Guid Id, Guid TenantId) From<TData>(PlatformEvent<TData> envelope)
        where TData : class => (envelope.Id, envelope.TenantId!.Value);
}
