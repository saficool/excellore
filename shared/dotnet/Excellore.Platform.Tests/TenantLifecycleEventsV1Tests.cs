using System.Text.Json;
using Excellore.Contracts;

namespace Excellore.Platform.Tests;

public sealed class TenantLifecycleEventsV1Tests
{
    private static readonly string[] Types =
    [
        TenantReactivatedV1.EventType,
        TenantDeletionRequestedV1.EventType,
        TenantDeletedV1.EventType
    ];

    [Fact]
    public void Each_tenant_event_requires_matching_nonempty_tenant_ids()
    {
        var tenant = Guid.NewGuid();
        foreach (var type in Types)
        {
            var data = Data(type, tenant);
            var parsed = Parse(type, EventJson(type, tenant, data));
            Assert.Equal(tenant, parsed.TenantId);

            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, EventJson(type, Guid.NewGuid(), data)));
            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, EventJson(type, null, data)));
            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, EventJson(type, Guid.Empty, data)));

            data["tenantId"] = Guid.Empty;
            Assert.Throws<InvalidPlatformEventException>(() =>
                Parse(type, EventJson(type, tenant, data)));
        }
    }

    [Fact]
    public void Reactivation_requires_bounded_reason_code()
    {
        var tenant = Guid.NewGuid();
        foreach (var reason in new[] { "", "free text", "UPPER", new string('a', 65) })
        {
            var data = Data(TenantReactivatedV1.EventType, tenant);
            data["reasonCode"] = reason;
            Assert.Throws<InvalidPlatformEventException>(() =>
                TenantReactivatedV1.Parse(EventJson(TenantReactivatedV1.EventType, tenant, data)));
        }
    }

    [Fact]
    public void Deletion_request_requires_a_future_due_date()
    {
        var tenant = Guid.NewGuid();
        var data = Data(TenantDeletionRequestedV1.EventType, tenant);
        data["dueAt"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.Throws<InvalidPlatformEventException>(() =>
            TenantDeletionRequestedV1.Parse(EventJson(TenantDeletionRequestedV1.EventType, tenant, data)));
        data.Remove("dueAt");
        Assert.Throws<InvalidPlatformEventException>(() =>
            TenantDeletionRequestedV1.Parse(EventJson(TenantDeletionRequestedV1.EventType, tenant, data)));
    }

    internal static Dictionary<string, object> Data(string type, Guid tenantId)
    {
        var data = new Dictionary<string, object> { ["tenantId"] = tenantId };
        if (type == TenantReactivatedV1.EventType) data["reasonCode"] = "review_complete";
        if (type == TenantDeletionRequestedV1.EventType)
            data["dueAt"] = DateTimeOffset.UtcNow.AddDays(14);
        return data;
    }

    internal static byte[] EventJson(string type, Guid? tenantId, Dictionary<string, object> data)
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

    internal static (Guid Id, Guid TenantId) Parse(string type, byte[] json) => type switch
    {
        TenantReactivatedV1.EventType => From(TenantReactivatedV1.Parse(json)),
        TenantDeletionRequestedV1.EventType => From(TenantDeletionRequestedV1.Parse(json)),
        TenantDeletedV1.EventType => From(TenantDeletedV1.Parse(json)),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static (Guid Id, Guid TenantId) From<TData>(PlatformEvent<TData> envelope)
        where TData : class => (envelope.Id, envelope.TenantId!.Value);
}
