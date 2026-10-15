using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class TenantDeletionRequestResponses
{
    internal static IResult Map(TenantDeletionRequestResult result) => result.Status switch
    {
        TenantDeletionRequestStatus.Accepted => Results.Json(new { result.DueAt },
            statusCode: StatusCodes.Status202Accepted),
        TenantDeletionRequestStatus.Invalid => Problem(400, "invalid-deletion-request", "Invalid deletion request"),
        TenantDeletionRequestStatus.Forbidden => Problem(403, "deletion-request-denied", "Deletion request denied"),
        TenantDeletionRequestStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        TenantDeletionRequestStatus.NotFound => Results.NotFound(),
        TenantDeletionRequestStatus.Conflict => Problem(409, "deletion-request-conflict", "Deletion request unavailable"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string type, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{type}", title: title);
}
