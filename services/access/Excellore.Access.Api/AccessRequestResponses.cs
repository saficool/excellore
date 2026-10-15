using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class AccessRequestResponses
{
    internal static IResult Map(AccessRequestResult result) => result.Status switch
    {
        AccessRequestStatus.Created => Results.Json(new { result.RequestId },
            statusCode: StatusCodes.Status201Created),
        AccessRequestStatus.Approved or AccessRequestStatus.Rejected => Results.NoContent(),
        AccessRequestStatus.Invalid => Problem(400, "invalid-access-request", "Invalid access request"),
        AccessRequestStatus.Forbidden => Problem(403, "access-request-denied", "Access request denied"),
        AccessRequestStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        AccessRequestStatus.NotFound => Results.NotFound(),
        AccessRequestStatus.Conflict => Problem(409, "access-request-conflict", "Access request unavailable"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string type, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{type}", title: title);
}
