using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class InvitationIssuanceResponses
{
    internal static IResult Map(InvitationIssuanceResult result) => result.Status switch
    {
        InvitationIssuanceStatus.Accepted => Results.Accepted(),
        InvitationIssuanceStatus.Invalid => Problem(400, "invalid-invitation", "Invalid invitation"),
        InvitationIssuanceStatus.Forbidden => Problem(403, "invitation-denied", "Invitation denied"),
        InvitationIssuanceStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        InvitationIssuanceStatus.NotFound => Results.NotFound(),
        InvitationIssuanceStatus.Conflict => Problem(409, "invitation-conflict", "Invitation unavailable"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string type, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{type}", title: title);
}
