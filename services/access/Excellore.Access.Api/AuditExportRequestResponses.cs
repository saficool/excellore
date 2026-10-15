using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class AuditExportRequestResponses
{
    internal static IResult Map(AuditExportRequestResult result) => result.Status switch
    {
        AuditExportRequestStatus.Accepted => Results.Json(new { result.ExportId },
            statusCode: StatusCodes.Status202Accepted),
        AuditExportRequestStatus.Forbidden => Problem(403, "audit-export-denied", "Audit export denied"),
        AuditExportRequestStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        AuditExportRequestStatus.NotFound => Results.NotFound(),
        AuditExportRequestStatus.Conflict => Problem(409, "audit-export-conflict", "Audit export already pending"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string type, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{type}", title: title);
}
