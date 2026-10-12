using Microsoft.AspNetCore.Http;

namespace Excellore.Platform;

public static class ExcelloreProblems
{
    public static IResult Unauthorized(HttpContext http) =>
        Create(http, StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized");

    public static IResult NotFound(HttpContext http) =>
        Create(http, StatusCodes.Status404NotFound, "not-found", "Not Found");

    public static IResult Forbidden(HttpContext http) =>
        Create(http, StatusCodes.Status403Forbidden, "forbidden", "Forbidden");

    public static IResult StepUpRequired(HttpContext http) =>
        Create(http, StatusCodes.Status403Forbidden, "step-up-required", "Step-up required");

    public static IResult AuthzUnavailable(HttpContext http) =>
        Create(http, StatusCodes.Status503ServiceUnavailable, "authz-unavailable", "Authorization unavailable");

    private static IResult Create(HttpContext http, int status, string name, string title) =>
        Results.Problem(
            statusCode: status,
            type: $"urn:excellore:problem:{name}",
            title: title,
            extensions: new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier });
}
