using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class SupportGrantResponses
{
    internal static IResult Map(SupportGrantResult result) => result.Status switch
    {
        SupportGrantStatus.Created => Results.Json(new { result.GrantId },
            statusCode: StatusCodes.Status201Created),
        SupportGrantStatus.Approved or SupportGrantStatus.Revoked => Results.NoContent(),
        SupportGrantStatus.Invalid => Problem(400, "invalid-support-grant", "Invalid support grant"),
        SupportGrantStatus.Forbidden => Problem(403, "support-grant-denied", "Support grant denied"),
        SupportGrantStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        SupportGrantStatus.NotFound => Results.NotFound(),
        SupportGrantStatus.Conflict => Problem(409, "support-grant-conflict", "Support grant unavailable"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string type, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{type}", title: title);
}
