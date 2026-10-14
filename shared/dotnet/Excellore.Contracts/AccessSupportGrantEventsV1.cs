using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record AccessSupportGrantCreatedV1
{
    public const string EventType = "access.supportgrant.created.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("grantId")]
    public Guid GrantId { get; init; }

    [JsonPropertyName("staffUserId")]
    public Guid StaffUserId { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }

    public static PlatformEvent<AccessSupportGrantCreatedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<AccessSupportGrantCreatedV1>(json, EventType,
            EventSource, true);
        AccessSupportGrantEventDataValidator.Validate(json, envelope.Data!.TenantId,
            envelope.Data.GrantId, envelope.Data.StaffUserId, envelope.Data.ExpiresAt,
            envelope.TenantId);
        if (envelope.Data.ExpiresAt <= envelope.Time)
            throw new InvalidPlatformEventException();
        return envelope;
    }
}

public sealed record AccessSupportGrantRevokedV1
{
    public const string EventType = "access.supportgrant.revoked.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("grantId")]
    public Guid GrantId { get; init; }

    [JsonPropertyName("staffUserId")]
    public Guid StaffUserId { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }

    public static PlatformEvent<AccessSupportGrantRevokedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<AccessSupportGrantRevokedV1>(json, EventType,
            EventSource, true);
        AccessSupportGrantEventDataValidator.Validate(json, envelope.Data!.TenantId,
            envelope.Data.GrantId, envelope.Data.StaffUserId, envelope.Data.ExpiresAt,
            envelope.TenantId);
        return envelope;
    }
}

internal static class AccessSupportGrantEventDataValidator
{
    public static void Validate(ReadOnlySpan<byte> json, Guid tenantId, Guid grantId,
        Guid staffUserId, DateTimeOffset expiresAt, Guid? envelopeTenantId)
    {
        if (tenantId != envelopeTenantId || grantId == Guid.Empty ||
            staffUserId == Guid.Empty || expiresAt == default)
            throw new InvalidPlatformEventException();

        using var document = JsonDocument.Parse(json.ToArray());
        var data = document.RootElement.GetProperty("data");
        EventTimestampValidator.RequireOffsetTimestamp(data, "expiresAt");
        foreach (var forbidden in new[]
        {
            "name", "description", "staffName", "email", "reason", "permissions", "roleName"
        })
        {
            if (data.TryGetProperty(forbidden, out _))
                throw new InvalidPlatformEventException();
        }
    }
}
