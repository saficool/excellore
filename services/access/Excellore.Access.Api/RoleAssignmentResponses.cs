using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class RoleAssignmentResponses
{
    internal static IResult Map(RoleAssignmentResult result) => result.Status switch
    {
        RoleAssignmentStatus.Granted => Results.Json(new { result.AssignmentId },
            statusCode: StatusCodes.Status201Created),
        RoleAssignmentStatus.Revoked => Results.NoContent(),
        RoleAssignmentStatus.Invalid => Problem(400, "invalid-assignment", "Invalid role assignment"),
        RoleAssignmentStatus.Forbidden => Problem(403, "assignment-denied", "Role assignment denied"),
        RoleAssignmentStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        RoleAssignmentStatus.NotFound => Results.NotFound(),
        RoleAssignmentStatus.Conflict => Problem(409, "assignment-conflict", "Role assignment conflict"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string type, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{type}", title: title);
}
