using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Excellore.Authz;

public static class ValkeyAuthzResolverRegistration
{
    /// <summary>Composes the Access source, Valkey snapshot, local cache, and replica invalidation subscriber.</summary>
    public static IServiceCollection AddValkeyAuthzResolver(this IServiceCollection services,
        IConfiguration configuration, string appPrefix,
        bool allowInsecureTokenEndpointForLocalDevelopment = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ValkeyAuthzCache.ValidateAppPrefix(appPrefix);
        if (!services.Any(item => item.ServiceType == typeof(IConnectionMultiplexer)))
        {
            throw new ArgumentException("Register a Valkey IConnectionMultiplexer before the resolver.",
                nameof(services));
        }
        if (services.Any(item => item.ServiceType == typeof(ValkeyAuthzInvalidationFanout)))
        {
            throw new InvalidOperationException("The Valkey authorization resolver is already registered.");
        }

        services.AddAccessAuthzClient(configuration,
            allowInsecureTokenEndpointForLocalDevelopment);
        services.AddCachedAuthzResolver();

        services.AddSingleton(provider => new ValkeyAuthzCache(
            provider.GetRequiredService<IConnectionMultiplexer>(), appPrefix));
        services.AddSingleton<IAuthzDistributedSnapshotCache>(provider =>
            provider.GetRequiredService<ValkeyAuthzCache>());
        services.Replace(ServiceDescriptor.Singleton<IAuthzSnapshotSource>(provider =>
            new ValkeyAuthzSnapshotSource(
                provider.GetRequiredService<IAuthzDistributedSnapshotCache>(),
                provider.GetRequiredService<HttpAccessAuthzSource>())));

        services.AddSingleton(provider => new ValkeyAuthzInvalidationFanout(
            provider.GetRequiredService<CachedAuthzResolver>(),
            provider.GetRequiredService<IAuthzDistributedSnapshotCache>(),
            provider.GetRequiredService<IConnectionMultiplexer>(), appPrefix));
        services.Replace(ServiceDescriptor.Singleton<IAuthzCacheInvalidator>(provider =>
            provider.GetRequiredService<ValkeyAuthzInvalidationFanout>()));
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<ValkeyAuthzInvalidationFanout>());
        return services;
    }
}
