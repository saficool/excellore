using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class GroupResponses
{
    internal static IResult Create(GroupMutationResult result) => result.Status == GroupMutationStatus.Created
        ? Results.Json(new { groupId = result.Id }, statusCode: StatusCodes.Status201Created)
        : Map(result);

    internal static IResult GrantRole(GroupMutationResult result) => result.Status == GroupMutationStatus.Created
        ? Results.Json(new { assignmentId = result.Id }, statusCode: StatusCodes.Status201Created)
        : Map(result);

    internal static IResult Map(GroupMutationResult result) => result.Status switch
    {
        GroupMutationStatus.Changed or GroupMutationStatus.Unchanged => Results.NoContent(),
        GroupMutationStatus.Invalid => Problem(400, "invalid-group-change", "Invalid group change"),
        GroupMutationStatus.Forbidden => Problem(403, "group-change-denied", "Group change denied"),
        GroupMutationStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        GroupMutationStatus.NotFound => Results.NotFound(),
        GroupMutationStatus.Conflict => Problem(409, "group-conflict", "Group change conflict"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string type, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{type}", title: title);
}
