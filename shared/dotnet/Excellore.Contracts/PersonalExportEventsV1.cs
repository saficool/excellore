using System.Text.Json.Serialization;
using System.Text.Json;

namespace Excellore.Contracts;

public sealed record ProfileExportRequestedV1
{
    public const string EventType = "profile.export.requested.v1";
    public const string EventSource = "excellore/profile";
    [JsonPropertyName("userId")] public Guid UserId { get; init; }
    [JsonPropertyName("exportId")] public Guid ExportId { get; init; }

    public static PlatformEvent<ProfileExportRequestedV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<ProfileExportRequestedV1>(json,
            EventType, EventSource, false);
        if (envelope.Data!.UserId == Guid.Empty || envelope.Data.ExportId == Guid.Empty)
            throw new InvalidPlatformEventException();
        PersonalExportEventValidator.EnsureOpaque(json);
        return envelope;
    }
}

public sealed record AccessUserExportReadyV1
{
    public const string EventType = "access.user-export.ready.v1";
    public const string EventSource = "excellore/access";
    [JsonPropertyName("userId")] public Guid UserId { get; init; }
    [JsonPropertyName("exportId")] public Guid ExportId { get; init; }

    public static PlatformEvent<AccessUserExportReadyV1> Parse(ReadOnlySpan<byte> json)
    {
        var envelope = PlatformEventParser.Parse<AccessUserExportReadyV1>(json,
            EventType, EventSource, false);
        if (envelope.Data!.UserId == Guid.Empty || envelope.Data.ExportId == Guid.Empty)
            throw new InvalidPlatformEventException();
        PersonalExportEventValidator.EnsureOpaque(json);
        return envelope;
    }
}

internal static class PersonalExportEventValidator
{
    public static void EnsureOpaque(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var data = document.RootElement.GetProperty("data");
        if (data.EnumerateObject().Any(property => property.Name is
            "name" or "displayName" or "givenName" or "familyName" or "email" or
            "locale" or "timeZone" or "avatar" or "avatarUrl" or "preferences" or
            "downloadUrl" or "exportUrl" or "signedUrl" or "url" or "objectKey" or "token"))
            throw new InvalidPlatformEventException();
    }
}
