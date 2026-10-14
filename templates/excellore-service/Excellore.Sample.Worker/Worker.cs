using Excellore.Platform;
using Npgsql;

namespace Excellore.Sample.Worker;

// Use a dedicated publisher DB role: SELECT outbox, UPDATE published_at only.
// Tenant event handlers use PostgresEventInbox and TenantWorkerScope; add them with their contracts.
public sealed class Worker(
    IConfiguration configuration,
    IEventBus bus,
    PostgresOutboxDispatcher dispatcher,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("Publisher") ??
            throw new InvalidOperationException("Publisher database configuration is required.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var publisher = await bus.CreatePublisherAsync(stoppingToken);
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var sent = await dispatcher.DispatchOnceAsync(connection, publisher, stoppingToken);
                    if (!sent) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Exception messages may contain connection details or event data.
                logger.LogWarning("Outbox dispatch unavailable; retrying");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
