using System.Net;
using Excellore.Platform.Testing;

namespace Excellore.Platform.Tests;

public sealed class TenantApiIsolationChecksTests
{
    private const string TenantPath = "/v1/tenants/{tenantId}/items/{itemId}";
    private const string OpenApi = """
        {
          "openapi": "3.1.0",
          "paths": {
            "/v1/tenants/{tenantId}/items/{itemId}": {
              "get": { "x-excellore-tenant-scoped": true }
            },
            "/v1/me": { "get": {} }
          }
        }
        """;
    private const string ListOpenApi = """
        {
          "openapi": "3.1.0",
          "paths": {
            "/v1/tenants/{tenantId}/items": {
              "get": { "x-excellore-tenant-scoped": true }
            }
          }
        }
        """;

    [Fact]
    public async Task Runs_all_required_scenarios_for_every_marked_operation()
    {
        var seen = new HashSet<TenantApiIsolationScenario>();
        var probe = Probe((scenario, _) =>
        {
            seen.Add(scenario);
            var status = scenario switch
            {
                TenantApiIsolationScenario.MissingPermission => HttpStatusCode.Forbidden,
                TenantApiIsolationScenario.Authorized => HttpStatusCode.OK,
                _ => HttpStatusCode.NotFound
            };
            return Task.FromResult(new HttpResponseMessage(status));
        });

        await TenantApiIsolationChecks.VerifyAsync(OpenApi, [probe]);

        Assert.Equal(Enum.GetValues<TenantApiIsolationScenario>().ToHashSet(), seen);
    }

    [Fact]
    public async Task Requires_a_probe_and_marker_for_each_tenant_route()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TenantApiIsolationChecks.VerifyAsync(OpenApi, []));

        var missingMarker = OpenApi.Replace("\"x-excellore-tenant-scoped\": true", "");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TenantApiIsolationChecks.VerifyAsync(missingMarker, [Probe(Success)]));

        var falseMarker = OpenApi.Replace("\"x-excellore-tenant-scoped\": true",
            "\"x-excellore-tenant-scoped\": false");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TenantApiIsolationChecks.VerifyAsync(falseMarker, [Probe(Success)]));
    }

    [Fact]
    public async Task Fails_on_a_leak_or_unexpected_denial()
    {
        var leak = Probe((scenario, _) => Task.FromResult(new HttpResponseMessage(
            scenario == TenantApiIsolationScenario.ForeignResourceMultiTenantMember
                ? HttpStatusCode.OK
                : scenario == TenantApiIsolationScenario.MissingPermission
                    ? HttpStatusCode.Forbidden
                    : scenario == TenantApiIsolationScenario.Authorized
                        ? HttpStatusCode.OK : HttpStatusCode.NotFound)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TenantApiIsolationChecks.VerifyAsync(OpenApi, [leak]));
        Assert.Contains(nameof(TenantApiIsolationScenario.ForeignResourceMultiTenantMember),
            error.Message);

        var wrongDenial = Probe((scenario, _) => Task.FromResult(new HttpResponseMessage(
            scenario == TenantApiIsolationScenario.Authorized
                ? HttpStatusCode.OK : HttpStatusCode.NotFound)));
        error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TenantApiIsolationChecks.VerifyAsync(OpenApi, [wrongDenial]));
        Assert.Contains(nameof(TenantApiIsolationScenario.MissingPermission), error.Message);

        var alwaysDenies = Probe((scenario, _) => Task.FromResult(new HttpResponseMessage(
            scenario == TenantApiIsolationScenario.MissingPermission
                ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound)));
        error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TenantApiIsolationChecks.VerifyAsync(OpenApi, [alwaysDenies]));
        Assert.Contains(nameof(TenantApiIsolationScenario.Authorized), error.Message);
    }

    [Fact]
    public async Task Skips_only_explicitly_inapplicable_scenarios()
    {
        var seen = new HashSet<TenantApiIsolationScenario>();
        var probe = Probe((scenario, _) =>
        {
            seen.Add(scenario);
            return Task.FromResult(new HttpResponseMessage(
                scenario == TenantApiIsolationScenario.Authorized
                    ? HttpStatusCode.OK : HttpStatusCode.NotFound));
        }) with
        {
            OpenApiPath = "/v1/tenants/{tenantId}/items",
            HasResourceReference = false,
            RequiresPermission = false
        };

        await TenantApiIsolationChecks.VerifyAsync(ListOpenApi, [probe]);

        Assert.Equal(6, seen.Count);
        Assert.DoesNotContain(TenantApiIsolationScenario.MissingPermission, seen);
        Assert.DoesNotContain(TenantApiIsolationScenario.ForeignResourceOtherTenantMember, seen);
        Assert.DoesNotContain(TenantApiIsolationScenario.ForeignResourceMultiTenantMember, seen);
    }

    [Fact]
    public async Task Requires_resource_checks_when_route_has_another_path_parameter()
    {
        var probe = Probe(Success) with { HasResourceReference = false };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TenantApiIsolationChecks.VerifyAsync(OpenApi, [probe]));
        Assert.Contains("resource probe is required", error.Message);
    }

    private static TenantApiIsolationProbe Probe(
        Func<TenantApiIsolationScenario, CancellationToken, Task<HttpResponseMessage>> sendAsync) =>
        new("GET", TenantPath, sendAsync, HasResourceReference: true);

    private static Task<HttpResponseMessage> Success(TenantApiIsolationScenario scenario,
        CancellationToken _) => Task.FromResult(new HttpResponseMessage(
        scenario == TenantApiIsolationScenario.MissingPermission
            ? HttpStatusCode.Forbidden
            : scenario == TenantApiIsolationScenario.Authorized
                ? HttpStatusCode.OK : HttpStatusCode.NotFound));
}
