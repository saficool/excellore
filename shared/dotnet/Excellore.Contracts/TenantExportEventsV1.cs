using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record TenantExportRequestedV1
{
    public const string EventType = "tenant.export.requested.v1";
    public const string EventSource = "excellore/access";
    [JsonPropertyName("tenantId")] public Guid TenantId { get; init; }
    [JsonPropertyName("exportId")] public Guid ExportId { get; init; }
    public static PlatformEvent<TenantExportRequestedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantExportRequestedV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId == Guid.Empty || envelope.Data.TenantId != envelope.TenantId ||
            envelope.Data.ExportId == Guid.Empty) throw new InvalidPlatformEventException();
        PersonalExportEventValidator.EnsureOpaque(json);
        return envelope;
    }
}

public sealed record ProfileTenantExportReadyV1
{
    public const string EventType = "profile.tenant-export.ready.v1";
    public const string EventSource = "excellore/profile";
    [JsonPropertyName("tenantId")] public Guid TenantId { get; init; }
    [JsonPropertyName("exportId")] public Guid ExportId { get; init; }
    public static PlatformEvent<ProfileTenantExportReadyV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<ProfileTenantExportReadyV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId == Guid.Empty || envelope.Data.TenantId != envelope.TenantId ||
            envelope.Data.ExportId == Guid.Empty) throw new InvalidPlatformEventException();
        PersonalExportEventValidator.EnsureOpaque(json);
        return envelope;
    }
}
