using System.Net;
using System.Text.Json;

namespace Excellore.Platform.Testing;

public enum TenantApiIsolationScenario
{
    NonMember,
    OtherTenantMember,
    ForeignResourceOtherTenantMember,
    ForeignResourceMultiTenantMember,
    MissingPermission,
    UnknownTenant,
    SuspendedTenant,
    RemovedMember,
    Authorized
}

public sealed record TenantApiIsolationProbe(
    string Method,
    string OpenApiPath,
    Func<TenantApiIsolationScenario, CancellationToken, Task<HttpResponseMessage>> SendAsync,
    bool HasResourceReference,
    bool RequiresPermission = true);

/// <summary>Runs the handbook's tenant API isolation matrix against every marked OpenAPI operation.</summary>
public static class TenantApiIsolationChecks
{
    private static readonly TenantApiIsolationScenario[] CoreScenarios =
    [
        TenantApiIsolationScenario.NonMember,
        TenantApiIsolationScenario.OtherTenantMember,
        TenantApiIsolationScenario.UnknownTenant,
        TenantApiIsolationScenario.SuspendedTenant,
        TenantApiIsolationScenario.RemovedMember
    ];

    private static readonly TenantApiIsolationScenario[] ResourceScenarios =
    [
        TenantApiIsolationScenario.ForeignResourceOtherTenantMember,
        TenantApiIsolationScenario.ForeignResourceMultiTenantMember
    ];

    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"
    };

    public static async Task VerifyAsync(string openApiJson,
        IReadOnlyCollection<TenantApiIsolationProbe> probes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openApiJson);
        ArgumentNullException.ThrowIfNull(probes);

        var operations = ReadTenantOperations(openApiJson);
        if (operations.Count == 0)
        {
            throw new InvalidOperationException("No tenant-scoped OpenAPI operations were found.");
        }

        var byOperation = new Dictionary<(string Method, string Path), TenantApiIsolationProbe>();
        foreach (var probe in probes)
        {
            ArgumentNullException.ThrowIfNull(probe);
            ArgumentNullException.ThrowIfNull(probe.SendAsync);
            ArgumentException.ThrowIfNullOrWhiteSpace(probe.Method);
            ArgumentException.ThrowIfNullOrWhiteSpace(probe.OpenApiPath);
            if (!probe.HasResourceReference &&
                probe.OpenApiPath.Replace("{tenantId}", "", StringComparison.Ordinal).Contains('{'))
            {
                throw new InvalidOperationException(
                    $"A resource probe is required for {probe.Method.ToUpperInvariant()} {probe.OpenApiPath}.");
            }
            var key = (probe.Method.ToUpperInvariant(), probe.OpenApiPath);
            if (!byOperation.TryAdd(key, probe))
            {
                throw new InvalidOperationException($"Duplicate tenant isolation probe: {key.Item1} {key.Item2}.");
            }
        }

        if (!operations.SetEquals(byOperation.Keys))
        {
            var missing = operations.Except(byOperation.Keys).OrderBy(item => item.Path).ThenBy(item => item.Method).FirstOrDefault();
            if (missing != default)
            {
                throw new InvalidOperationException($"Missing tenant isolation probe: {missing.Method} {missing.Path}.");
            }

            var extra = byOperation.Keys.Except(operations).OrderBy(item => item.Path).ThenBy(item => item.Method).First();
            throw new InvalidOperationException($"Unmatched tenant isolation probe: {extra.Method} {extra.Path}.");
        }

        foreach (var (method, path) in operations.OrderBy(item => item.Path).ThenBy(item => item.Method))
        {
            var probe = byOperation[(method, path)];
            foreach (var scenario in CoreScenarios)
            {
                await CheckAsync(probe, scenario, HttpStatusCode.NotFound, cancellationToken);
            }

            if (probe.HasResourceReference)
            {
                foreach (var scenario in ResourceScenarios)
                {
                    await CheckAsync(probe, scenario, HttpStatusCode.NotFound, cancellationToken);
                }
            }

            if (probe.RequiresPermission)
            {
                await CheckAsync(probe, TenantApiIsolationScenario.MissingPermission,
                    HttpStatusCode.Forbidden, cancellationToken);
            }

            await CheckAsync(probe, TenantApiIsolationScenario.Authorized, null,
                cancellationToken);
        }
    }

    private static async Task CheckAsync(TenantApiIsolationProbe probe,
        TenantApiIsolationScenario scenario, HttpStatusCode? expected,
        CancellationToken cancellationToken)
    {
        using var response = await probe.SendAsync(scenario, cancellationToken);
        if (response is null ||
            (expected is null ? !response.IsSuccessStatusCode : response.StatusCode != expected))
        {
            var expectation = expected is null ? "2xx" : ((int)expected).ToString();
            throw new InvalidOperationException(
                $"Tenant isolation failed for {probe.Method.ToUpperInvariant()} {probe.OpenApiPath}, {scenario}: expected {expectation}, received {(int?)response?.StatusCode}.");
        }
    }

    private static HashSet<(string Method, string Path)> ReadTenantOperations(string openApiJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(openApiJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("OpenAPI document is invalid.");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("paths", out var paths) ||
                paths.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("OpenAPI paths are required.");
            }

            var operations = new HashSet<(string Method, string Path)>();
            foreach (var path in paths.EnumerateObject())
            {
                if (path.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("OpenAPI path item is invalid.");
                }

                foreach (var operation in path.Value.EnumerateObject())
                {
                    if (!HttpMethods.Contains(operation.Name))
                    {
                        continue;
                    }

                    var marker = operation.Value.ValueKind == JsonValueKind.Object &&
                        operation.Value.TryGetProperty("x-excellore-tenant-scoped", out var value) &&
                        value.ValueKind == JsonValueKind.True;
                    var tenantPath = path.Name.Contains("{tenantId}", StringComparison.Ordinal);
                    if (marker != tenantPath)
                    {
                        throw new InvalidOperationException(
                            $"Tenant-scoped OpenAPI marker does not match {operation.Name.ToUpperInvariant()} {path.Name}.");
                    }

                    if (marker)
                    {
                        operations.Add((operation.Name.ToUpperInvariant(), path.Name));
                    }
                }
            }

            return operations;
        }
    }
}
