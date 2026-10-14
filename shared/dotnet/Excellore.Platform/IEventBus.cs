namespace Excellore.Platform;

public delegate Task PlatformEventHandler(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

/// <summary>Publishes versioned platform events with broker confirmation.</summary>
public interface IEventPublisher : IAsyncDisposable
{
    Task PublishAsync(Guid eventId, string eventType, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default);
}

/// <summary>Creates event publishers and subscribes service queues.</summary>
public interface IEventBus
{
    Task<IEventPublisher> CreatePublisherAsync(CancellationToken cancellationToken = default);

    Task<IAsyncDisposable> SubscribeAsync(string serviceName,
        IReadOnlyDictionary<string, PlatformEventHandler> handlers,
        CancellationToken cancellationToken = default);
}
