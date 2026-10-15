using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class CustomRoleResponses
{
    internal static IResult Map(CustomRoleResult result) => result.Status switch
    {
        CustomRoleStatus.Created => Results.Json(new { result.RoleId },
            statusCode: StatusCodes.Status201Created),
        CustomRoleStatus.Updated or CustomRoleStatus.Deleted or CustomRoleStatus.Unchanged =>
            Results.NoContent(),
        CustomRoleStatus.Invalid => Problem(400, "invalid-role", "Invalid custom role"),
        CustomRoleStatus.Forbidden => Problem(403, "role-escalation-denied", "Role change denied"),
        CustomRoleStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        CustomRoleStatus.NotFound => Results.NotFound(),
        CustomRoleStatus.Conflict => Problem(409, "role-name-taken", "Role name unavailable"),
        CustomRoleStatus.LimitReached => Problem(409, "role-limit-reached", "Custom role limit reached"),
        CustomRoleStatus.InUse => Problem(409, "role-in-use", "Role is still assigned"),
        CustomRoleStatus.ConfigurationUnavailable => Problem(503, "role-limit-unavailable",
            "Role limit unavailable"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string type, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{type}", title: title);
}
