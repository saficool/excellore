using Excellore.Platform;
using Npgsql;

namespace Excellore.Access.Worker;

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
                // Exception text can include broker credentials or event metadata.
                logger.LogWarning("Access outbox publication unavailable; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
