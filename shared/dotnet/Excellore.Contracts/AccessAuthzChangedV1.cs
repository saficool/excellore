using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record AccessAuthzChangedV1
{
    public const string EventType = "access.authz.changed.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("userIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid[]? UserIds { get; init; }

    [JsonPropertyName("authzVersion")]
    public long AuthzVersion { get; init; }

    public static PlatformEvent<AccessAuthzChangedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = TenantEventParser.Parse<AccessAuthzChangedV1>(json, EventType, EventSource);
        if (envelope.Data!.TenantId != envelope.TenantId ||
            envelope.Data.AuthzVersion < 1)
        {
            throw new InvalidPlatformEventException();
        }

        return envelope;
    }
}
