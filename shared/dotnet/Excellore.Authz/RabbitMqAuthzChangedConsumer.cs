using Excellore.Contracts;
using Excellore.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Excellore.Authz;

/// <summary>Consumes Access authorization changes after the Valkey fanout subscriber starts.</summary>
public sealed class RabbitMqAuthzChangedConsumer(
    IEventBus eventBus, AccessAuthzChangedHandler handler, string serviceName) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = await eventBus.SubscribeAsync(serviceName,
            new Dictionary<string, PlatformEventHandler>
            {
                [AccessAuthzChangedV1.EventType] = handler.HandleAsync
            }, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
            _subscription = null;
        }
    }
}

public static class RabbitMqAuthzChangedConsumerRegistration
{
    public static IServiceCollection AddRabbitMqAuthzChangedConsumer(this IServiceCollection services,
        string serviceName, Uri brokerUri)
    {
        ArgumentNullException.ThrowIfNull(services);
        ValkeyAuthzCache.ValidateAppPrefix(serviceName);
        if (!services.Any(item => item.ServiceType == typeof(ValkeyAuthzInvalidationFanout)))
        {
            throw new InvalidOperationException("Register the Valkey authorization resolver before broker consumption.");
        }
        if (services.Any(item => item.ServiceType == typeof(RabbitMqAuthzChangedConsumer)))
        {
            throw new InvalidOperationException("The authorization-change consumer is already registered.");
        }

        services.TryAddSingleton<IEventBus>(new RabbitMqEventBus(brokerUri));
        services.AddSingleton(provider => new RabbitMqAuthzChangedConsumer(
            provider.GetRequiredService<IEventBus>(),
            provider.GetRequiredService<AccessAuthzChangedHandler>(), serviceName));
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<RabbitMqAuthzChangedConsumer>());
        return services;
    }
}
