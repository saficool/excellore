using Excellore.Auth;
using Excellore.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Excellore.Authz;

public sealed class TenantGateFilter(
    IAuthzResolver resolver,
    ICurrentUser user,
    ITenantContextAccessor tenantContext,
    IPrivilegeClassifier privilegeClassifier,
    ILogger<TenantGateFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return ExcelloreProblems.Unauthorized(http);
        }

        if (!Guid.TryParse(http.Request.RouteValues["tenantId"]?.ToString(), out var tenantId) ||
            tenantId == Guid.Empty)
        {
            return ExcelloreProblems.NotFound(http);
        }

        var metadata = http.GetEndpoint()?.Metadata;
        var requirements = metadata?.GetOrderedMetadata<RequirePermissionMetadata>()
            .SelectMany(item => item.Permissions).ToArray() ?? [];
        var allowAnyMember = metadata?.GetMetadata<AllowAnyMemberMetadata>() is not null;
        if ((requirements.Length == 0 && !allowAnyMember) ||
            (requirements.Length > 0 && allowAnyMember))
        {
            return ExcelloreProblems.Forbidden(http);
        }

        Guid userId;
        try
        {
            userId = user.UserId;
        }
        catch (InvalidOperationException)
        {
            return ExcelloreProblems.Unauthorized(http);
        }

        AuthzSnapshot snapshot;
        try
        {
            snapshot = await resolver.ResolveAsync(userId, tenantId, http.RequestAborted);
        }
        catch (AuthzUnavailableException)
        {
            return ExcelloreProblems.AuthzUnavailable(http);
        }

        if (snapshot.TenantId != tenantId || !snapshot.IsActiveMember || snapshot.Permissions is null)
        {
            return ExcelloreProblems.NotFound(http);
        }

        if (requirements.Any(permission => !snapshot.Permissions.Contains(permission)))
        {
            return ExcelloreProblems.Forbidden(http);
        }

        try
        {
            foreach (var permission in requirements)
            {
                if (await privilegeClassifier.IsPrivilegedAsync(permission, http.RequestAborted) &&
                    !user.HasRecentMfa)
                {
                    return ExcelloreProblems.StepUpRequired(http);
                }
            }
        }
        catch (AuthzUnavailableException)
        {
            return ExcelloreProblems.AuthzUnavailable(http);
        }

        tenantContext.Set(tenantId, snapshot);
        try
        {
            var tenantTag = tenantId.ToString("D");
            http.Features.Get<IHttpActivityFeature>()?.Activity.SetTag("tenant.id", tenantTag);
            using (logger.BeginScope(new Dictionary<string, object> { ["tenant.id"] = tenantTag }))
            {
                return await next(context);
            }
        }
        finally
        {
            tenantContext.Clear();
        }
    }
}
