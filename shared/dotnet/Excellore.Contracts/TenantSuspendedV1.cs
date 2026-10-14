using System.Text.Json;
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
        var envelope = PlatformEventParser.Parse<TenantSuspendedV1>(json, EventType, EventSource, true);
        if (envelope.Data!.TenantId != envelope.TenantId ||
            !TenantEventDataValidator.IsReasonCode(envelope.Data.ReasonCode))
        {
            throw new InvalidPlatformEventException();
        }

        return envelope;
    }
}

internal static class TenantEventDataValidator
{
    private static readonly HashSet<string> ForbiddenMetadataValues =
    ["slug", "previousSlug", "name", "plan", "apps", "features"];

    public static bool IsReasonCode(string? value) => IsMachineCode(value);

    public static void RejectMetadataValues(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        if (document.RootElement.GetProperty("data").EnumerateObject()
            .Any(property => ForbiddenMetadataValues.Contains(property.Name)))
            throw new InvalidPlatformEventException();
    }

    public static bool IsMachineCode(string? value)
    {
        if (value is null or { Length: < 1 or > 64 } || value[0] is < 'a' or > 'z')
        {
            return false;
        }

        return value.Skip(1).All(character => character is >= 'a' and <= 'z' or
            >= '0' and <= '9' or '_' or '-');
    }
}
