using System.Text.Json;
using Excellore.Contracts;

namespace Excellore.Platform.Tests;

public sealed class TenantSuspendedV1Tests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void Parses_valid_event_and_ignores_unknown_fields()
    {
        var original = Event();
        var parsed = TenantSuspendedV1.Parse(JsonSerializer.SerializeToUtf8Bytes(original));
        Assert.Equal(Tenant, parsed.TenantId);
        Assert.Equal("platform_security", parsed.Data!.ReasonCode);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(original));
        var properties = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value);
        properties["futureField"] = JsonDocument.Parse("true").RootElement.Clone();
        var future = TenantSuspendedV1.Parse(JsonSerializer.SerializeToUtf8Bytes(properties));
        Assert.Equal(original.Id, future.Id);
    }

    [Fact]
    public void Rejects_wrong_tenant_type_source_actor_or_reason_code()
    {
        var original = Event();
        var broken = new[]
        {
            original with { TenantId = null },
            original with { TenantId = Guid.NewGuid() },
            original with { Type = "tenant.reactivated.v1" },
            original with { Source = "excellore/profile" },
            original with { Actor = null },
            original with { Data = original.Data! with { ReasonCode = "free text with spaces" } },
            original with { Data = original.Data! with { ReasonCode = "" } },
            original with { Data = original.Data! with { ReasonCode = new string('a', 65) } }
        };
        foreach (var invalid in broken)
        {
            var error = Assert.Throws<InvalidPlatformEventException>(() =>
                TenantSuspendedV1.Parse(JsonSerializer.SerializeToUtf8Bytes(invalid)));
            Assert.Equal("Invalid platform event.", error.Message);
        }
    }

    [Fact]
    public void Rejects_malformed_and_oversized_event_without_exposing_input()
    {
        Assert.Throws<InvalidPlatformEventException>(() => TenantSuspendedV1.Parse("not json"u8));
        Assert.Throws<InvalidPlatformEventException>(() =>
            TenantSuspendedV1.Parse(new byte[64 * 1024 + 1]));
    }

    internal static PlatformEvent<TenantSuspendedV1> Event() => new()
    {
        SpecVersion = "1.0",
        Id = Guid.NewGuid(),
        Type = TenantSuspendedV1.EventType,
        Source = TenantSuspendedV1.EventSource,
        Time = DateTimeOffset.UtcNow,
        TenantId = Tenant,
        CorrelationId = Guid.NewGuid(),
        Actor = new EventActor { Sub = Guid.NewGuid().ToString("D"), Type = "machine" },
        Data = new TenantSuspendedV1 { TenantId = Tenant, ReasonCode = "platform_security" }
    };
}
