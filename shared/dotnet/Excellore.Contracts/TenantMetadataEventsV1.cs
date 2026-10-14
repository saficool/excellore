using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record TenantCreatedV1
{
    public const string EventType = "tenant.created.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    public static PlatformEvent<TenantCreatedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantCreatedV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId != envelope.TenantId)
            throw new InvalidPlatformEventException();
        TenantEventDataValidator.RejectMetadataValues(json);
        return envelope;
    }
}

public sealed record TenantUpdatedV1
{
    public const string EventType = "tenant.updated.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("changed")]
    public string[]? Changed { get; init; }

    public static PlatformEvent<TenantUpdatedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantUpdatedV1>(json, EventType, EventSource, true);
        var changed = envelope.Data!.Changed;
        if (envelope.Data.TenantId != envelope.TenantId || changed is null || changed.Length == 0 ||
            changed.Distinct(StringComparer.Ordinal).Count() != changed.Length ||
            changed.Any(field => field is not ("slug" or "name" or "plan")))
            throw new InvalidPlatformEventException();
        TenantEventDataValidator.RejectMetadataValues(json);
        return envelope;
    }
}

public sealed record TenantAppEnabledV1
{
    public const string EventType = "tenant.app.enabled.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("appId")]
    public string? AppId { get; init; }

    public static PlatformEvent<TenantAppEnabledV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantAppEnabledV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId != envelope.TenantId ||
            !TenantEventDataValidator.IsMachineCode(envelope.Data.AppId))
            throw new InvalidPlatformEventException();
        TenantEventDataValidator.RejectMetadataValues(json);
        return envelope;
    }
}

public sealed record TenantAppDisabledV1
{
    public const string EventType = "tenant.app.disabled.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("appId")]
    public string? AppId { get; init; }

    public static PlatformEvent<TenantAppDisabledV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantAppDisabledV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId != envelope.TenantId ||
            !TenantEventDataValidator.IsMachineCode(envelope.Data.AppId))
            throw new InvalidPlatformEventException();
        TenantEventDataValidator.RejectMetadataValues(json);
        return envelope;
    }
}
