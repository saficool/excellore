using System.Threading.RateLimiting;
using Excellore.Auth;
using Microsoft.AspNetCore.Http;

namespace Excellore.Authz;

public sealed record TenantRateLimitOptions(int UserPermitLimit = 60,
    int TenantPermitLimit = 600, TimeSpan? Window = null);

public sealed class TenantRateLimitPolicy : IDisposable
{
    private readonly PartitionedRateLimiter<(Guid UserId, Guid TenantId)> limiter;

    public TenantRateLimitPolicy(TenantRateLimitOptions options)
    {
        if (options.UserPermitLimit < 1 || options.TenantPermitLimit < 1 ||
            options.Window is { } window && window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        var interval = options.Window ?? TimeSpan.FromMinutes(1);
        limiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<(Guid UserId, Guid TenantId), Guid>(key =>
                RateLimitPartition.GetFixedWindowLimiter(key.UserId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.UserPermitLimit, Window = interval,
                    QueueLimit = 0, AutoReplenishment = true
                })),
            PartitionedRateLimiter.Create<(Guid UserId, Guid TenantId), Guid>(key =>
                RateLimitPartition.GetFixedWindowLimiter(key.TenantId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.TenantPermitLimit, Window = interval,
                    QueueLimit = 0, AutoReplenishment = true
                })));
    }

    public ValueTask<RateLimitLease> AcquireAsync(Guid userId, Guid tenantId,
        CancellationToken cancellationToken = default) =>
        limiter.AcquireAsync((userId, tenantId), 1, cancellationToken);

    public void Dispose() => limiter.Dispose();
}

public sealed class TenantRateLimitFilter(ICurrentUser user,
    ITenantContextAccessor tenant, TenantRateLimitPolicy policy) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        using var lease = await policy.AcquireAsync(user.UserId, tenant.TenantId,
            context.HttpContext.RequestAborted);
        if (!lease.IsAcquired)
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
                type: "urn:excellore:problem:rate-limited", title: "Too many requests");
        return await next(context);
    }
}
