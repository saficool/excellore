namespace Excellore.Access.Application;

public sealed record RoleGrant(Guid RoleId, string Name, DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

public sealed record RolePermission(Guid RoleId, string Key, string AppId);

public sealed record EffectiveAccess(bool Allowed, string[] Roles, string[] Permissions, string[] Apps)
{
    public static EffectiveAccess Denied { get; } = new(false, [], [], []);
}

public static class EffectivePermissionCalculator
{
    public static EffectiveAccess Resolve(bool tenantActive, bool memberActive,
        IEnumerable<RoleGrant> grants, IEnumerable<RolePermission> rolePermissions,
        IEnumerable<string> enabledApps, DateTimeOffset now)
    {
        if (!tenantActive || !memberActive) return EffectiveAccess.Denied;

        var activeGrants = grants.Where(grant => grant.RevokedAt is null &&
            (grant.ExpiresAt is null || grant.ExpiresAt > now)).ToArray();
        var roleIds = activeGrants.Select(grant => grant.RoleId).ToHashSet();
        var enabled = enabledApps.ToHashSet(StringComparer.Ordinal);
        var permissions = rolePermissions
            .Where(permission => roleIds.Contains(permission.RoleId) && enabled.Contains(permission.AppId))
            .Select(permission => permission.Key).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var effectiveApps = enabled
            .Where(app => permissions.Contains($"{app}.app.access", StringComparer.Ordinal))
            .Append("shell").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new EffectiveAccess(true,
            activeGrants.Select(grant => grant.Name).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(),
            permissions, effectiveApps);
    }
}
