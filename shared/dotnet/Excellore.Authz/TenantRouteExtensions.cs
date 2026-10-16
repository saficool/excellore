using System.Collections.Immutable;
using Excellore.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Excellore.Authz;

public sealed record RequirePermissionMetadata(ImmutableArray<string> Permissions);

public sealed class AllowAnyMemberMetadata
{
    internal static readonly AllowAnyMemberMetadata Instance = new();
    private AllowAnyMemberMetadata() { }
}

internal sealed class TenantGateMetadata
{
    internal static readonly TenantGateMetadata Instance = new();
    private TenantGateMetadata() { }
}

public static class TenantRouteExtensions
{
    public static IServiceCollection AddExcelloreAuthz(this IServiceCollection services)
    {
        services.AddScoped<TenantContextAccessor>();
        services.AddScoped<ITenantContextAccessor>(provider => provider.GetRequiredService<TenantContextAccessor>());
        services.AddScoped<ITenantIdAccessor>(provider => provider.GetRequiredService<TenantContextAccessor>());
        services.TryAddSingleton<IPrivilegeClassifier, ConservativePrivilegeClassifier>();
        services.TryAddSingleton(new TenantRateLimitOptions());
        services.TryAddSingleton<TenantRateLimitPolicy>();
        services.TryAddSingleton(new TenantProbingOptions());
        services.TryAddSingleton<TenantProbingDetector>();
        return services;
    }

    public static RouteGroupBuilder AddTenantGate(this RouteGroupBuilder group)
    {
        group.RequireAuthorization();
        group.AddEndpointFilter<TenantGateFilter>();
        group.WithMetadata(TenantGateMetadata.Instance);
        return group;
    }

    public static RouteGroupBuilder AddTenantRateLimit(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter<TenantRateLimitFilter>();
        return group;
    }

    public static RouteGroupBuilder AddSupportGrantUsageAudit(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter<SupportGrantUsageFilter>();
        return group;
    }

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, params string[] permissions)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Length == 0 || permissions.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one valid permission is required.", nameof(permissions));
        }

        builder.WithMetadata(new RequirePermissionMetadata(ImmutableArray.CreateRange(permissions)));
        return builder;
    }

    public static TBuilder AllowAnyMember<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(AllowAnyMemberMetadata.Instance);
        return builder;
    }

    public static void AssertTenantRoutesProtected(this IEndpointRouteBuilder routes)
    {
        foreach (var endpoint in routes.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            if (!endpoint.RoutePattern.Parameters.Any(parameter =>
                    string.Equals(parameter.Name, "tenantId", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var metadata = endpoint.Metadata;
            var hasPermission = metadata.GetOrderedMetadata<RequirePermissionMetadata>()
                .Any(requirement => !requirement.Permissions.IsDefaultOrEmpty);
            var allowAnyMember = metadata.GetMetadata<AllowAnyMemberMetadata>() is not null;
            var authorized = metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0 &&
                metadata.GetMetadata<IAllowAnonymous>() is null;

            if (metadata.GetMetadata<TenantGateMetadata>() is null || !authorized ||
                hasPermission == allowAnyMember)
            {
                throw new InvalidOperationException(
                    $"Tenant route '{endpoint.RoutePattern.RawText}' requires a tenant gate, authorization, and exactly one permission rule.");
            }
        }
    }
}
