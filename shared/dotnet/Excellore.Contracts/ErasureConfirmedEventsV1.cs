using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public abstract record ErasureConfirmationDataV1
{
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("tenantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TenantId { get; init; }

    [JsonPropertyName("userId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? UserId { get; init; }

    [JsonPropertyName("service")]
    public string? Service { get; init; }
}

public sealed record AccessErasureConfirmedV1 : ErasureConfirmationDataV1
{
    public const string EventType = "access.erasure.confirmed.v1";
    public const string EventSource = "excellore/access";
    public const string ServiceCode = "access";

    public static PlatformEvent<AccessErasureConfirmedV1> Parse(ReadOnlySpan<byte> json) =>
        ErasureConfirmationEventParser.Parse<AccessErasureConfirmedV1>(json, EventType,
            EventSource, ServiceCode);
}

public sealed record ProfileErasureConfirmedV1 : ErasureConfirmationDataV1
{
    public const string EventType = "profile.erasure.confirmed.v1";
    public const string EventSource = "excellore/profile";
    public const string ServiceCode = "profile";

    public static PlatformEvent<ProfileErasureConfirmedV1> Parse(ReadOnlySpan<byte> json) =>
        ErasureConfirmationEventParser.Parse<ProfileErasureConfirmedV1>(json, EventType,
            EventSource, ServiceCode);
}

internal static class ErasureConfirmationEventParser
{
    private const int MaxEventBytes = 64 * 1024;

    public static PlatformEvent<TData> Parse<TData>(ReadOnlySpan<byte> json, string eventType,
        string eventSource, string serviceCode) where TData : ErasureConfirmationDataV1
    {
        if (json.Length == 0 || json.Length > MaxEventBytes)
            throw new InvalidPlatformEventException();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException)
        {
            throw new InvalidPlatformEventException();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("scope", out var scopeValue) ||
                scopeValue.ValueKind != JsonValueKind.String)
                throw new InvalidPlatformEventException();

            var scope = scopeValue.GetString();
            if (scope is not ("tenant" or "user"))
                throw new InvalidPlatformEventException();

            var envelope = PlatformEventParser.Parse<TData>(json, eventType, eventSource,
                scope == "tenant");
            var payload = envelope.Data!;
            if (payload.Service != serviceCode || payload.Scope != scope)
                throw new InvalidPlatformEventException();

            if (scope == "tenant")
            {
                if (payload.TenantId is not Guid tenantId || tenantId == Guid.Empty ||
                    payload.TenantId != envelope.TenantId ||
                    data.TryGetProperty("userId", out _))
                    throw new InvalidPlatformEventException();
            }
            else if (payload.UserId is not Guid userId || userId == Guid.Empty ||
                data.TryGetProperty("tenantId", out _))
                throw new InvalidPlatformEventException();

            if (data.EnumerateObject().Any(property => property.Name is
                "name" or "email" or "reason" or "objectKey" or "downloadUrl" or
                "token" or "details"))
                throw new InvalidPlatformEventException();

            return envelope;
        }
    }
}
