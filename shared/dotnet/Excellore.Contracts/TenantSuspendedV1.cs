using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record TenantSuspendedV1
{
    public const string EventType = "tenant.suspended.v1";
    public const string EventSource = "excellore/access";

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; init; }

    public static PlatformEvent<TenantSuspendedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = TenantEventParser.Parse<TenantSuspendedV1>(json, EventType, EventSource);
        if (envelope.Data!.TenantId != envelope.TenantId || !IsReasonCode(envelope.Data.ReasonCode))
        {
            throw new InvalidPlatformEventException();
        }

        return envelope;
    }

    private static bool IsReasonCode(string? value)
    {
        if (value is null or { Length: < 1 or > 64 } || value[0] is < 'a' or > 'z')
        {
            return false;
        }

        return value.Skip(1).All(character => character is >= 'a' and <= 'z' or
            >= '0' and <= '9' or '_' or '-');
    }
}
