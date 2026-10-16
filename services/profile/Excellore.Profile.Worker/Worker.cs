using Excellore.Platform;
using Npgsql;

namespace Excellore.Profile.Worker;

/// <summary>Publishes confirmed global Profile events using the restricted publisher role.</summary>
public sealed class Worker(NpgsqlDataSource publisherDb, IEventBus eventBus,
    PostgresOutboxDispatcher dispatcher, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var publisher = await eventBus.CreatePublisherAsync(stoppingToken);
                await using var connection = await publisherDb.OpenConnectionAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (!await dispatcher.DispatchOnceAsync(connection, publisher, stoppingToken))
                        await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Broker and database exceptions may contain credentials or event metadata.
                logger.LogWarning("Profile outbox publication unavailable; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
