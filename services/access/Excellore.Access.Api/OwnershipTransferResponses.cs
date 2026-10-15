using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class OwnershipTransferResponses
{
    internal static IResult Map(OwnershipTransferResult result) => result.Status switch
    {
        OwnershipTransferStatus.Created => Results.Json(new { result.TransferId },
            statusCode: StatusCodes.Status201Created),
        OwnershipTransferStatus.Accepted => Results.NoContent(),
        OwnershipTransferStatus.Invalid => Problem(400, "invalid-ownership-transfer", "Invalid ownership transfer"),
        OwnershipTransferStatus.Forbidden => Problem(403, "ownership-transfer-denied", "Ownership transfer denied"),
        OwnershipTransferStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        OwnershipTransferStatus.NotFound => Results.NotFound(),
        OwnershipTransferStatus.Conflict => Problem(409, "ownership-transfer-conflict", "Ownership transfer unavailable"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string type, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{type}", title: title);
}
