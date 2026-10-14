using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record AccessRoleCreatedV1
{
    public const string EventType = "access.role.created.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("roleId")]
    public Guid RoleId { get; init; }

    public static PlatformEvent<AccessRoleCreatedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<AccessRoleCreatedV1>(json, EventType, EventSource, true);
        AccessRoleEventDataValidator.Validate(json, envelope.Data!.TenantId, envelope.Data.RoleId,
            envelope.TenantId);
        return envelope;
    }
}

public sealed record AccessRoleUpdatedV1
{
    public const string EventType = "access.role.updated.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("roleId")]
    public Guid RoleId { get; init; }

    public static PlatformEvent<AccessRoleUpdatedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<AccessRoleUpdatedV1>(json, EventType, EventSource, true);
        AccessRoleEventDataValidator.Validate(json, envelope.Data!.TenantId, envelope.Data.RoleId,
            envelope.TenantId);
        return envelope;
    }
}

public sealed record AccessRoleDeletedV1
{
    public const string EventType = "access.role.deleted.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("roleId")]
    public Guid RoleId { get; init; }

    public static PlatformEvent<AccessRoleDeletedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<AccessRoleDeletedV1>(json, EventType, EventSource, true);
        AccessRoleEventDataValidator.Validate(json, envelope.Data!.TenantId, envelope.Data.RoleId,
            envelope.TenantId);
        return envelope;
    }
}

internal static class AccessRoleEventDataValidator
{
    public static void Validate(ReadOnlySpan<byte> json, Guid tenantId, Guid roleId,
        Guid? envelopeTenantId)
    {
        if (tenantId != envelopeTenantId || roleId == Guid.Empty)
            throw new InvalidPlatformEventException();

        using var document = JsonDocument.Parse(json.ToArray());
        var data = document.RootElement.GetProperty("data");
        if (data.TryGetProperty("name", out _) || data.TryGetProperty("description", out _) ||
            data.TryGetProperty("permissions", out _))
            throw new InvalidPlatformEventException();
    }
}
