using Excellore.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Excellore.Authz;

public interface ISupportGrantUsageRecorder
{
    Task RecordIfSupportAsync(Guid tenantId, Guid userId, string method,
        string routePattern, string? acr, CancellationToken cancellationToken);
}

public sealed class SupportGrantUsageFilter(ICurrentUser user,
    ITenantContextAccessor tenant, ISupportGrantUsageRecorder recorder) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var endpoint = context.HttpContext.GetEndpoint() as RouteEndpoint;
        await recorder.RecordIfSupportAsync(tenant.TenantId, user.UserId,
            context.HttpContext.Request.Method, endpoint?.RoutePattern.RawText ?? "unknown",
            user.Acr, context.HttpContext.RequestAborted);
        return await next(context);
    }
}
