using Excellore.Access.Infrastructure;

namespace Excellore.Access.Api;

internal static class TenantSettingsResponses
{
    internal static IResult Map(TenantSettingsResult result) => result.Status switch
    {
        TenantSettingsStatus.Changed => Results.Json(result.Settings),
        TenantSettingsStatus.Invalid => Problem(400, "invalid-tenant-settings", "Invalid tenant settings"),
        TenantSettingsStatus.Forbidden => Problem(403, "tenant-settings-denied", "Tenant settings change denied"),
        TenantSettingsStatus.StepUpRequired => Problem(403, "step-up-required", "Step-up required"),
        TenantSettingsStatus.NotFound => Results.NotFound(),
        TenantSettingsStatus.Conflict => Problem(409, "tenant-slug-taken", "Tenant slug is unavailable"),
        _ => Results.StatusCode(500)
    };

    private static IResult Problem(int status, string type, string title) =>
        Results.Problem(statusCode: status, type: $"urn:excellore:problem:{type}", title: title);
}
