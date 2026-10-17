using Excellore.Contracts;
using Excellore.Notifications.Infrastructure;
using Excellore.Platform;

namespace Excellore.Notifications.Worker;

public sealed class NotificationSubscriber(IEventBus eventBus,
    NotificationEventProcessor processor, ILogger<NotificationSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var handlers = new Dictionary<string, PlatformEventHandler>
        {
            [AccessAssignmentGrantedV1.EventType] = processor.HandleAssignmentGrantedAsync
        };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var subscription = await eventBus.SubscribeAsync(
                    "notifications", handlers, stoppingToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                logger.LogWarning("Notification subscription unavailable; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
