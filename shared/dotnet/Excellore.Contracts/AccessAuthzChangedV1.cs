using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record AccessAuthzChangedV1
{
    public const string EventType = "access.authz.changed.v1";
    public const string EventSource = "excellore/access";
    private const int MaxEventBytes = 64 * 1024;

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("userIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid[]? UserIds { get; init; }

    [JsonPropertyName("authzVersion")]
    public long AuthzVersion { get; init; }

    public static PlatformEvent<AccessAuthzChangedV1> Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length == 0 || json.Length > MaxEventBytes)
        {
            throw new InvalidPlatformEventException();
        }

        PlatformEvent<AccessAuthzChangedV1>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<PlatformEvent<AccessAuthzChangedV1>>(json);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new InvalidPlatformEventException();
        }

        if (envelope is null || envelope.SpecVersion != "1.0" || envelope.Id == Guid.Empty ||
            envelope.Type != EventType || envelope.Source != EventSource ||
            envelope.Time == default || envelope.TenantId == Guid.Empty ||
            envelope.CorrelationId == Guid.Empty || envelope.Actor is null ||
            string.IsNullOrWhiteSpace(envelope.Actor.Sub) ||
            envelope.Actor.Type is not ("human" or "machine") ||
            envelope.Data is null || envelope.Data.TenantId != envelope.TenantId ||
            envelope.Data.AuthzVersion < 1)
        {
            throw new InvalidPlatformEventException();
        }

        return envelope;
    }
}

public sealed class InvalidPlatformEventException : Exception
{
    public InvalidPlatformEventException() : base("Invalid platform event.") { }
}
