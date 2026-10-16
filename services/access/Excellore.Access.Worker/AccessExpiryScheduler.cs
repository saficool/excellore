using Excellore.Access.Infrastructure;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Access.Worker;

public sealed class AccessWorkerDatabase(NpgsqlDataSource source)
{
    public NpgsqlDataSource Source { get; } = source;
}

/// <summary>Runs a locked, bounded tenant sweep every minute.</summary>
public sealed class AccessExpiryScheduler(AccessWorkerDatabase database,
    TenantJobScheduler scheduler, IConfiguration configuration,
    ILogger<AccessExpiryScheduler> logger) : BackgroundService
{
    private const long ScheduleLock = 0x4558435745585052;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Guid.TryParse(configuration["Worker:ActorId"], out var machineActorId) ||
            machineActorId == Guid.Empty)
            throw new InvalidOperationException("Worker:ActorId must be a machine identity UUID.");

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await SweepAsync(machineActorId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Database errors may include private row data or credentials.
                logger.LogWarning("Access expiry sweep unavailable; will retry");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SweepAsync(Guid machineActorId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.Source.OpenConnectionAsync(cancellationToken);
        await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
        acquire.Parameters.AddWithValue("key", ScheduleLock);
        if ((bool)(await acquire.ExecuteScalarAsync(cancellationToken))! is false) return;
        try
        {
            var tenantIds = new List<Guid>();
            await using (var tenants = new NpgsqlCommand(
                "SELECT id FROM tenants WHERE status<>'deleted' ORDER BY id", connection))
            await using (var reader = await tenants.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) tenantIds.Add(reader.GetGuid(0));

            await scheduler.RunAsync<AccessTenantMaintenanceJob>(tenantIds, 4,
                (job, token) => job.RunAsync(machineActorId, token), cancellationToken);
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            release.Parameters.AddWithValue("key", ScheduleLock);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
