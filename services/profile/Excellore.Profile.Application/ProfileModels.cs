namespace Excellore.Profile.Application;

public sealed record UpdateOwnProfile(string? FirstName, string? LastName, string? Locale,
    string? DisplayName, string? TimeZone);
public sealed record OwnProfile(Guid UserId, string? FirstName, string? LastName, string? Locale,
    string? DisplayName, string TimeZone, bool HasAvatar);
public sealed record UpdatePreferences(string? Language, string? TimeZone, string? DateFormat,
    bool? Notifications, string? Theme);
public sealed record MemberProfile(Guid UserId, string? JobTitle, string? Department,
    string? WorkPhone, string DirectoryVisibility);
public sealed record UpdateMemberProfile(string? JobTitle, string? Department,
    string? WorkPhone, string DirectoryVisibility);
public sealed record AdminMemberUpdate(Guid UserId, string? JobTitle, string? Department,
    string? WorkPhone, string DirectoryVisibility, string Reason);
public sealed record MemberLookup(Guid UserId);
public sealed record CreateAvatarUpload(string ContentType, int SizeBytes);
public sealed record CompleteAvatarUpload(string Base64Data);

public static class ProfileValidation
{
    public static bool ValidOwn(UpdateOwnProfile input) =>
        Short(input.FirstName, 80) && Short(input.LastName, 80) && Short(input.Locale, 35) &&
        Short(input.DisplayName, 120) && Short(input.TimeZone, 80) &&
        (input.TimeZone is null || TimeZoneInfo.TryFindSystemTimeZoneById(input.TimeZone, out _));

    public static bool ValidMember(UpdateMemberProfile input) =>
        Short(input.JobTitle, 120) && Short(input.Department, 120) && Short(input.WorkPhone, 40) &&
        input.DirectoryVisibility is "visible" or "hidden";

    public static bool ValidPreferences(UpdatePreferences input) =>
        Short(input.Language, 35) && Short(input.TimeZone, 80) &&
        (input.TimeZone is null || TimeZoneInfo.TryFindSystemTimeZoneById(input.TimeZone, out _)) &&
        (input.DateFormat is null or "iso" or "local") &&
        (input.Theme is null or "system" or "light" or "dark");

    private static bool Short(string? value, int max) => value is null ||
        (value.Length <= max && !value.Any(char.IsControl));
}
