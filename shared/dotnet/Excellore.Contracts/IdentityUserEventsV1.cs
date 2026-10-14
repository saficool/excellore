using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record IdentityUserCreatedV1
{
    public const string EventType = "identity.user.created.v1";
    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    public static PlatformEvent<IdentityUserCreatedV1> Parse(ReadOnlySpan<byte> json) =>
        IdentityUserEventParser.Parse<IdentityUserCreatedV1>(json, EventType, data => data.UserId);
}

public sealed record IdentityUserUpdatedV1
{
    public const string EventType = "identity.user.updated.v1";
    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    [JsonPropertyName("changed")]
    public string[]? Changed { get; init; }

    public static PlatformEvent<IdentityUserUpdatedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = IdentityUserEventParser.Parse<IdentityUserUpdatedV1>(json, EventType,
            data => data.UserId);
        var changed = envelope.Data!.Changed;
        if (changed is null || changed.Length == 0 || changed.Distinct(StringComparer.Ordinal).Count() != changed.Length ||
            changed.Any(field => field is not ("givenName" or "familyName" or "locale" or "email")))
        {
            throw new InvalidPlatformEventException();
        }

        return envelope;
    }
}

public sealed record IdentityUserDisabledV1
{
    public const string EventType = "identity.user.disabled.v1";
    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    public static PlatformEvent<IdentityUserDisabledV1> Parse(ReadOnlySpan<byte> json) =>
        IdentityUserEventParser.Parse<IdentityUserDisabledV1>(json, EventType, data => data.UserId);
}

public sealed record IdentityUserEnabledV1
{
    public const string EventType = "identity.user.enabled.v1";
    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    public static PlatformEvent<IdentityUserEnabledV1> Parse(ReadOnlySpan<byte> json) =>
        IdentityUserEventParser.Parse<IdentityUserEnabledV1>(json, EventType, data => data.UserId);
}

public sealed record IdentityUserDeletedV1
{
    public const string EventType = "identity.user.deleted.v1";
    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    public static PlatformEvent<IdentityUserDeletedV1> Parse(ReadOnlySpan<byte> json) =>
        IdentityUserEventParser.Parse<IdentityUserDeletedV1>(json, EventType, data => data.UserId);
}

internal static class IdentityUserEventParser
{
    private const string EventSource = "excellore/access";

    public static PlatformEvent<TData> Parse<TData>(ReadOnlySpan<byte> json, string eventType,
        Func<TData, Guid> userId) where TData : class
    {
        var envelope = PlatformEventParser.Parse<TData>(json, eventType, EventSource, false);
        if (userId(envelope.Data!) == Guid.Empty)
        {
            throw new InvalidPlatformEventException();
        }

        using var document = JsonDocument.Parse(json.ToArray());
        var data = document.RootElement.GetProperty("data");
        if (data.EnumerateObject().Any(property => property.Name is
            "email" or "emailVerified" or "givenName" or "familyName" or "locale"))
        {
            throw new InvalidPlatformEventException();
        }

        return envelope;
    }
}
