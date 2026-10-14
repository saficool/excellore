using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record ProfileUpdatedV1
{
    public const string EventType = "profile.updated.v1";

    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    [JsonPropertyName("changed")]
    public string[]? Changed { get; init; }

    public static PlatformEvent<ProfileUpdatedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = ProfileEventDataValidator.Parse<ProfileUpdatedV1>(json, EventType,
            data => data.UserId);
        var changed = envelope.Data!.Changed;
        if (changed is null || changed.Length == 0 ||
            changed.Distinct(StringComparer.Ordinal).Count() != changed.Length ||
            changed.Any(field => field is not
                ("displayName" or "timeZone" or "avatar" or "preferences")))
            throw new InvalidPlatformEventException();
        return envelope;
    }
}

public sealed record ProfileDeletionRequestedV1
{
    public const string EventType = "profile.deletion.requested.v1";

    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    public static PlatformEvent<ProfileDeletionRequestedV1> Parse(ReadOnlySpan<byte> json) =>
        ProfileEventDataValidator.Parse<ProfileDeletionRequestedV1>(json, EventType,
            data => data.UserId);
}

public sealed record ProfileExportReadyV1
{
    public const string EventType = "profile.export.ready.v1";

    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    [JsonPropertyName("exportId")]
    public Guid ExportId { get; init; }

    public static PlatformEvent<ProfileExportReadyV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = ProfileEventDataValidator.Parse<ProfileExportReadyV1>(json, EventType,
            data => data.UserId);
        if (envelope.Data!.ExportId == Guid.Empty)
            throw new InvalidPlatformEventException();
        return envelope;
    }
}

internal static class ProfileEventDataValidator
{
    private const string EventSource = "excellore/profile";

    public static PlatformEvent<TData> Parse<TData>(ReadOnlySpan<byte> json, string eventType,
        Func<TData, Guid> userId) where TData : class
    {
        var envelope = PlatformEventParser.Parse<TData>(json, eventType, EventSource, false);
        if (userId(envelope.Data!) == Guid.Empty)
            throw new InvalidPlatformEventException();

        using var document = JsonDocument.Parse(json.ToArray());
        var data = document.RootElement.GetProperty("data");
        if (data.EnumerateObject().Any(property => property.Name is
            "name" or "displayName" or "givenName" or "familyName" or "email" or
            "locale" or "timeZone" or "avatar" or "avatarUrl" or "preferences" or
            "downloadUrl" or "exportUrl" or "signedUrl" or "url" or
            "objectKey" or "token"))
            throw new InvalidPlatformEventException();

        return envelope;
    }
}
