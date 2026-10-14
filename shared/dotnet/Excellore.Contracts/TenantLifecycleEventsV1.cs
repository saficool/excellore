using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record TenantReactivatedV1
{
    public const string EventType = "tenant.reactivated.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; init; }

    public static PlatformEvent<TenantReactivatedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantReactivatedV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId != envelope.TenantId ||
            !TenantEventDataValidator.IsReasonCode(envelope.Data.ReasonCode))
        {
            throw new InvalidPlatformEventException();
        }

        return envelope;
    }
}

public sealed record TenantDeletionRequestedV1
{
    public const string EventType = "tenant.deletion.requested.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("dueAt")]
    public DateTimeOffset DueAt { get; init; }

    public static PlatformEvent<TenantDeletionRequestedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantDeletionRequestedV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId != envelope.TenantId || envelope.Data.DueAt <= envelope.Time)
        {
            throw new InvalidPlatformEventException();
        }

        return envelope;
    }
}

public sealed record TenantDeletedV1
{
    public const string EventType = "tenant.deleted.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    public static PlatformEvent<TenantDeletedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantDeletedV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId != envelope.TenantId)
        {
            throw new InvalidPlatformEventException();
        }

        return envelope;
    }
}
