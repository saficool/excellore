using System.Text.RegularExpressions;

namespace Excellore.Access.Application;

public sealed record PermissionCatalog(string? App, IReadOnlyList<CatalogPermission>? Permissions);

public sealed record CatalogPermission(
    string? Key,
    string? Description,
    bool Privileged,
    bool OwnerOnly,
    bool Deprecated,
    IReadOnlyList<string>? SystemRoles);

public static class PermissionCatalogValidator
{
    public static readonly IReadOnlySet<string> RoleNames =
        new HashSet<string>(["owner", "admin", "member", "viewer", "auditor"], StringComparer.Ordinal);

    public static bool IsValid(PermissionCatalog? catalog, string app)
    {
        if (catalog?.App != app || !Regex.IsMatch(app, "^[a-z][a-z0-9-]{0,39}$") ||
            catalog.Permissions is not { Count: > 0 and <= 200 }) return false;

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in catalog.Permissions)
        {
            if (permission.Key is null ||
                !Regex.IsMatch(permission.Key, $"^{Regex.Escape(app)}\\.[a-z][a-z0-9]*(?:\\.[a-z][a-z0-9]*)+$") ||
                permission.Key.Length > 120 || !keys.Add(permission.Key) ||
                string.IsNullOrWhiteSpace(permission.Description) || permission.Description.Length > 240 ||
                (permission.SystemRoles?.Count ?? 0) > RoleNames.Count ||
                (permission.SystemRoles ?? []).Distinct(StringComparer.Ordinal).Count() != (permission.SystemRoles?.Count ?? 0) ||
                (permission.SystemRoles ?? []).Any(role => !RoleNames.Contains(role)) ||
                (!permission.Deprecated && !(permission.SystemRoles ?? []).Contains("owner", StringComparer.Ordinal)) ||
                (permission.OwnerOnly && (permission.SystemRoles ?? []).Any(role => role != "owner"))) return false;
        }

        return keys.Contains($"{app}.app.access");
    }
}
