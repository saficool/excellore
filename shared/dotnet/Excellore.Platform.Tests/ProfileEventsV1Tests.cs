using System.Text.Json;
using Excellore.Contracts;

namespace Excellore.Platform.Tests;

public sealed class ProfileEventsV1Tests
{
    private static readonly string[] Types =
    [
        ProfileUpdatedV1.EventType,
        ProfileDeletionRequestedV1.EventType,
        ProfileExportReadyV1.EventType
    ];

    [Fact]
    public void Global_profile_events_require_user_id_and_reject_tenant_id()
    {
        foreach (var type in Types)
        {
            var data = Data(type);
            Assert.Equal(data["userId"], Parse(type, EventJson(type, data)).UserId);
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                EventJson(type, data, Guid.NewGuid())));

            data["userId"] = Guid.Empty;
            Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                EventJson(type, data)));
        }
    }

    [Fact]
    public void Profile_events_reject_personal_values_and_links()
    {
        foreach (var type in Types)
        {
            var data = Data(type);
            foreach (var forbidden in new[]
            {
                "name", "displayName", "givenName", "familyName", "email", "locale",
                "timeZone", "avatar", "avatarUrl", "preferences", "downloadUrl",
                "exportUrl", "signedUrl", "url", "objectKey", "token"
            })
            {
                data[forbidden] = "personal value";
                Assert.Throws<InvalidPlatformEventException>(() => Parse(type,
                    EventJson(type, data)));
                data.Remove(forbidden);
            }
        }
    }

    [Fact]
    public void Profile_update_allows_only_unique_known_field_names()
    {
        var data = Data(ProfileUpdatedV1.EventType);
        foreach (var changed in new[]
        {
            Array.Empty<string>(), new[] { "avatar", "avatar" },
            new[] { "personal@example.test" }, new[] { "jobTitle" }
        })
        {
            data["changed"] = changed;
            Assert.Throws<InvalidPlatformEventException>(() => ProfileUpdatedV1.Parse(
                EventJson(ProfileUpdatedV1.EventType, data)));
        }

        data.Remove("changed");
        Assert.Throws<InvalidPlatformEventException>(() => ProfileUpdatedV1.Parse(
            EventJson(ProfileUpdatedV1.EventType, data)));
    }

    [Fact]
    public void Export_ready_requires_nonempty_export_id_and_no_download_url()
    {
        var data = Data(ProfileExportReadyV1.EventType);
        data["exportId"] = Guid.Empty;
        Assert.Throws<InvalidPlatformEventException>(() => ProfileExportReadyV1.Parse(
            EventJson(ProfileExportReadyV1.EventType, data)));

        data.Remove("exportId");
        Assert.Throws<InvalidPlatformEventException>(() => ProfileExportReadyV1.Parse(
            EventJson(ProfileExportReadyV1.EventType, data)));
    }

    [Fact]
    public void Profile_envelope_omits_tenant_id_when_serialized()
    {
        var original = new PlatformEvent<ProfileUpdatedV1>
        {
            SpecVersion = "1.0",
            Id = Guid.NewGuid(),
            Type = ProfileUpdatedV1.EventType,
            Source = "excellore/profile",
            Time = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
            Actor = new EventActor { Sub = Guid.NewGuid().ToString("D"), Type = "machine" },
            Data = new ProfileUpdatedV1
            {
                UserId = Guid.NewGuid(), Changed = ["displayName"]
            }
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("tenantid", out _));
        Assert.Null(ProfileUpdatedV1.Parse(json).TenantId);
    }

    internal static Dictionary<string, object> Data(string type)
    {
        var data = new Dictionary<string, object> { ["userId"] = Guid.NewGuid() };
        if (type == ProfileUpdatedV1.EventType) data["changed"] = new[] { "avatar" };
        if (type == ProfileExportReadyV1.EventType) data["exportId"] = Guid.NewGuid();
        return data;
    }

    internal static (Guid Id, Guid UserId) Parse(string type, byte[] json) => type switch
    {
        ProfileUpdatedV1.EventType => From(ProfileUpdatedV1.Parse(json), data => data.UserId),
        ProfileDeletionRequestedV1.EventType => From(ProfileDeletionRequestedV1.Parse(json),
            data => data.UserId),
        ProfileExportReadyV1.EventType => From(ProfileExportReadyV1.Parse(json),
            data => data.UserId),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static (Guid Id, Guid UserId) From<TData>(PlatformEvent<TData> envelope,
        Func<TData, Guid> userId) where TData : class =>
        (envelope.Id, userId(envelope.Data!));

    internal static byte[] EventJson(string type, Dictionary<string, object> data,
        Guid? tenantId = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid(),
            ["type"] = type,
            ["source"] = "excellore/profile",
            ["time"] = DateTimeOffset.UtcNow,
            ["correlationid"] = Guid.NewGuid(),
            ["actor"] = new { sub = Guid.NewGuid().ToString("D"), type = "machine" },
            ["data"] = data
        };
        if (tenantId is not null) envelope["tenantid"] = tenantId;
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }
}
