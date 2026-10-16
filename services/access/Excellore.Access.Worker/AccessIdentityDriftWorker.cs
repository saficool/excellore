using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Worker;

/// <summary>Compares the global Access identity mirror with Keycloak once a day.</summary>
public sealed class AccessIdentityDriftWorker(AccessWorkerDatabase database,
    IKeycloakDirectory directory, ILogger<AccessIdentityDriftWorker> logger) : BackgroundService
{
    private const long ScheduleLock = 0x4558435744524946;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                var count = await CountDriftAsync(stoppingToken);
                if (count > 0)
                    logger.LogWarning("Identity directory drift detected for {Count} records", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Directory and database errors can contain credentials or personal fields.
                logger.LogWarning("Identity drift check unavailable; will retry");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> CountDriftAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.Source.OpenConnectionAsync(cancellationToken);
        await using var lockCommand = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(@key)", connection);
        lockCommand.Parameters.AddWithValue("key", ScheduleLock);
        if ((bool)(await lockCommand.ExecuteScalarAsync(cancellationToken))! is false)
            return 0;
        try
        {
            var directoryUsers = await directory.ListAsync(cancellationToken);
            await using var query = new NpgsqlCommand(
                "SELECT user_id,status FROM identities", connection);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            var mismatches = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var userId = reader.GetGuid(0);
                var expectedActive = reader.GetString(1) == "active";
                if (!directoryUsers.TryGetValue(userId, out var actual) ||
                    actual.Enabled != expectedActive)
                    mismatches++;
            }
            return mismatches;
        }
        finally
        {
            await using var release = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(@key)", connection);
            release.Parameters.AddWithValue("key", ScheduleLock);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
