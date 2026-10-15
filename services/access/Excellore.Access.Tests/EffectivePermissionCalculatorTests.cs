using Excellore.Access.Application;

namespace Excellore.Access.Tests;

public sealed class EffectivePermissionCalculatorTests
{
    [Theory]
    [InlineData("active", true, true, false, false, true, true, true)]
    [InlineData("expired assignment", true, true, true, false, true, false, false)]
    [InlineData("revoked assignment", true, true, false, true, true, false, false)]
    [InlineData("disabled app", true, true, false, false, false, true, false)]
    [InlineData("suspended tenant", false, true, false, false, true, false, false)]
    [InlineData("removed member", true, false, false, false, true, false, false)]
    public void Effective_permissions_follow_tenant_member_grant_and_app_gates(
        string scenario, bool tenantActive, bool memberActive, bool expired, bool revoked,
        bool reportEnabled, bool accessAllowed, bool reportAllowed)
    {
        var now = DateTimeOffset.UtcNow;
        var roleId = Guid.NewGuid();
        var grants = new[]
        {
            new RoleGrant(roleId, "member", expired ? now : null, revoked ? now : null)
        };
        var permissions = new[]
        {
            new RolePermission(roleId, "access.app.access", "access"),
            new RolePermission(roleId, "reports.app.access", "reports"),
            new RolePermission(roleId, "reports.data.read", "reports")
        };
        var apps = reportEnabled ? new[] { "access", "reports" } : ["access"];

        var result = EffectivePermissionCalculator.Resolve(
            tenantActive, memberActive, grants, permissions, apps, now);

        Assert.True(result.Permissions.Contains("access.app.access") == accessAllowed, scenario);
        Assert.Equal(reportAllowed, result.Permissions.Contains("reports.app.access"));
        Assert.Equal(reportAllowed, result.Permissions.Contains("reports.data.read"));
        Assert.Equal(accessAllowed, result.Apps.Contains("access"));
        Assert.Equal(reportAllowed, result.Apps.Contains("reports"));
        Assert.Equal(tenantActive && memberActive, result.Apps.Contains("shell"));
        Assert.Equal(tenantActive && memberActive, result.Allowed);
    }

    [Fact]
    public void Group_and_direct_grants_union_without_duplicate_permissions()
    {
        var now = DateTimeOffset.UtcNow;
        var direct = Guid.NewGuid();
        var group = Guid.NewGuid();
        var result = EffectivePermissionCalculator.Resolve(true, true,
            [new(direct, "member", null, null), new(group, "auditor", now.AddMinutes(1), null)],
            [new(direct, "access.app.access", "access"),
             new(group, "access.app.access", "access"),
             new(group, "access.audit.read", "access")],
            ["access"], now);

        Assert.Equal(["auditor", "member"], result.Roles);
        Assert.Equal(["access.app.access", "access.audit.read"], result.Permissions);
        Assert.Equal(["access", "shell"], result.Apps);
    }
}
