using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record TenantMemberAddedV1
{
    public const string EventType = "tenant.member.added.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    [JsonPropertyName("membershipId")]
    public Guid MembershipId { get; init; }

    public static PlatformEvent<TenantMemberAddedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantMemberAddedV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId != envelope.TenantId || envelope.Data.UserId == Guid.Empty ||
            envelope.Data.MembershipId == Guid.Empty)
            throw new InvalidPlatformEventException();
        TenantMemberEventDataValidator.RejectRoleValues(json);
        return envelope;
    }
}

public sealed record TenantMemberSuspendedV1
{
    public const string EventType = "tenant.member.suspended.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    public static PlatformEvent<TenantMemberSuspendedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantMemberSuspendedV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId != envelope.TenantId || envelope.Data.UserId == Guid.Empty)
            throw new InvalidPlatformEventException();
        TenantMemberEventDataValidator.RejectRoleValues(json);
        return envelope;
    }
}

public sealed record TenantMemberReactivatedV1
{
    public const string EventType = "tenant.member.reactivated.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    public static PlatformEvent<TenantMemberReactivatedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantMemberReactivatedV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId != envelope.TenantId || envelope.Data.UserId == Guid.Empty)
            throw new InvalidPlatformEventException();
        TenantMemberEventDataValidator.RejectRoleValues(json);
        return envelope;
    }
}

public sealed record TenantMemberRemovedV1
{
    public const string EventType = "tenant.member.removed.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    public static PlatformEvent<TenantMemberRemovedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<TenantMemberRemovedV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId != envelope.TenantId || envelope.Data.UserId == Guid.Empty)
            throw new InvalidPlatformEventException();
        TenantMemberEventDataValidator.RejectRoleValues(json);
        return envelope;
    }
}

internal static class TenantMemberEventDataValidator
{
    public static void RejectRoleValues(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var data = document.RootElement.GetProperty("data");
        if (data.TryGetProperty("roles", out _) || data.TryGetProperty("roleNames", out _))
            throw new InvalidPlatformEventException();
    }
}
