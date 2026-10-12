using Excellore.Auth;
using Excellore.Platform;
using Microsoft.AspNetCore.Http;

namespace Excellore.Authz;

public sealed class TenantGateFilter(
    IAuthzResolver resolver,
    ICurrentUser user,
    ITenantContextAccessor tenantContext,
    IPrivilegeClassifier privilegeClassifier) : IEndpointFilter
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

        if (requirements.Any(privilegeClassifier.IsPrivileged) && !user.HasRecentMfa)
        {
            return ExcelloreProblems.StepUpRequired(http);
        }

        tenantContext.Set(tenantId, snapshot);
        try
        {
            return await next(context);
        }
        finally
        {
            tenantContext.Clear();
        }
    }
}
