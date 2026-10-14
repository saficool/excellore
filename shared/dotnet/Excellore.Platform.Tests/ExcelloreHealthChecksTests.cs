using Excellore.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Excellore.Platform.Tests;

public sealed class ExcelloreHealthChecksTests
{
    [Theory]
    [InlineData(HealthStatus.Healthy, StatusCodes.Status200OK)]
    [InlineData(HealthStatus.Unhealthy, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(HealthStatus.Degraded, StatusCodes.Status503ServiceUnavailable)]
    public async Task Liveness_and_readiness_report_only_overall_status(
        HealthStatus dependencyStatus, int expectedReadyStatus)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddExcelloreHealthChecks()
            .AddCheck("database", () => new HealthCheckResult(dependencyStatus,
                "connection string and database details must remain private"));

        await using var app = builder.Build();
        app.MapExcelloreHealthChecks();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToDictionary(endpoint => endpoint.RoutePattern.RawText!);

        var live = await InvokeAsync(endpoints["/health/live"], app.Services);
        Assert.Equal(StatusCodes.Status200OK, live.Status);
        Assert.Equal("Healthy", live.Body);

        var ready = await InvokeAsync(endpoints["/health/ready"], app.Services);
        Assert.Equal(expectedReadyStatus, ready.Status);
        Assert.Equal(dependencyStatus.ToString(), ready.Body);
    }

    private static async Task<(int Status, string Body)> InvokeAsync(
        RouteEndpoint endpoint, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        body.Position = 0;
        using var reader = new StreamReader(body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }
}
