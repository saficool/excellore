using Excellore.Authz;

namespace Excellore.Authz.Tests;

public sealed class TenantRateLimitPolicyTests
{
    [Fact]
    public async Task Limits_each_user_and_tenant_independently()
    {
        using var policy = new TenantRateLimitPolicy(new TenantRateLimitOptions(2, 3,
            TimeSpan.FromMinutes(1)));
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();
        var thirdUser = Guid.NewGuid();
        var firstTenant = Guid.NewGuid();
        var secondTenant = Guid.NewGuid();

        using var first = await policy.AcquireAsync(firstUser, firstTenant);
        using var second = await policy.AcquireAsync(firstUser, firstTenant);
        using var userLimited = await policy.AcquireAsync(firstUser, firstTenant);
        using var third = await policy.AcquireAsync(secondUser, firstTenant);
        using var tenantLimited = await policy.AcquireAsync(secondUser, firstTenant);
        using var otherTenant = await policy.AcquireAsync(thirdUser, secondTenant);

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(userLimited.IsAcquired);
        Assert.True(third.IsAcquired);
        Assert.False(tenantLimited.IsAcquired);
        Assert.True(otherTenant.IsAcquired);
    }
}
