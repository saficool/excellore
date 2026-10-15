using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class MemberLifecycleResponses
{
    internal static IResult Map(MemberLifecycleResult result) => result.Status switch
    {
        MemberLifecycleStatus.Changed => Results.NoContent(),
        MemberLifecycleStatus.Invalid => Problem(400, "invalid-member-change", "Invalid member change"),
        MemberLifecycleStatus.Forbidden => Problem(403, "member-change-denied", "Member change denied"),
        MemberLifecycleStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        MemberLifecycleStatus.NotFound => Results.NotFound(),
        MemberLifecycleStatus.Conflict => Problem(409, "member-change-conflict", "Member change unavailable"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string type, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{type}", title: title);
}
