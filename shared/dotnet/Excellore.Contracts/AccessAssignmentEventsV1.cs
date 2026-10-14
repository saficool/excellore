using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Excellore.Contracts;

public sealed record AccessAssignmentGrantedV1
{
    public const string EventType = "access.assignment.granted.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    [JsonPropertyName("roleId")]
    public Guid RoleId { get; init; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresAt { get; init; }

    public static PlatformEvent<AccessAssignmentGrantedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<AccessAssignmentGrantedV1>(json, EventType,
            EventSource, true);
        AccessAssignmentEventDataValidator.Validate(json, envelope.Data!.TenantId,
            envelope.Data.UserId, envelope.Data.RoleId, envelope.TenantId);
        return envelope;
    }
}

public sealed record AccessAssignmentRevokedV1
{
    public const string EventType = "access.assignment.revoked.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    [JsonPropertyName("roleId")]
    public Guid RoleId { get; init; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresAt { get; init; }

    public static PlatformEvent<AccessAssignmentRevokedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<AccessAssignmentRevokedV1>(json, EventType,
            EventSource, true);
        AccessAssignmentEventDataValidator.Validate(json, envelope.Data!.TenantId,
            envelope.Data.UserId, envelope.Data.RoleId, envelope.TenantId);
        return envelope;
    }
}

internal static class AccessAssignmentEventDataValidator
{
    public static void Validate(ReadOnlySpan<byte> json, Guid tenantId, Guid userId,
        Guid roleId, Guid? envelopeTenantId)
    {
        if (tenantId != envelopeTenantId || userId == Guid.Empty || roleId == Guid.Empty)
            throw new InvalidPlatformEventException();

        using var document = JsonDocument.Parse(json.ToArray());
        var data = document.RootElement.GetProperty("data");
        if (data.TryGetProperty("expiresAt", out var expiresAt))
        {
            if (expiresAt.ValueKind != JsonValueKind.String ||
                !Regex.IsMatch(expiresAt.GetString()!, @"(?:[Zz]|[+-]\d{2}:\d{2})$"))
                throw new InvalidPlatformEventException();
        }

        foreach (var forbidden in new[]
        {
            "name", "description", "roleName", "email", "reason", "permissions"
        })
        {
            if (data.TryGetProperty(forbidden, out _))
                throw new InvalidPlatformEventException();
        }
    }
}
