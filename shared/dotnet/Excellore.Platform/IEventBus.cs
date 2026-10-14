namespace Excellore.Platform;

public delegate Task PlatformEventHandler(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

/// <summary>Subscribes a service queue to versioned platform events.</summary>
public interface IEventBus
{
    Task<IAsyncDisposable> SubscribeAsync(string serviceName,
        IReadOnlyDictionary<string, PlatformEventHandler> handlers,
        CancellationToken cancellationToken = default);
}
