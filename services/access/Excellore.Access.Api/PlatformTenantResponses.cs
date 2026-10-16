using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class PlatformTenantResponses
{
    internal static IResult Map(PlatformTenantResult result) => result.Status switch
    {
        PlatformTenantStatus.Updated => result.DueAt is { } dueAt
            ? Results.Json(new { dueAt }) : Results.NoContent(),
        PlatformTenantStatus.Unchanged => Results.NoContent(),
        PlatformTenantStatus.Invalid => Problem(400, "invalid-platform-tenant", "Invalid tenant change"),
        PlatformTenantStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        PlatformTenantStatus.NotFound => Results.NotFound(),
        PlatformTenantStatus.Conflict => Problem(409, "platform-tenant-conflict", "Tenant change conflicts"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{code}", title: title);
}
