using System.Collections.Concurrent;

namespace Excellore.Platform;

/// <summary>Runs a tenant sweep with bounded parallelism and a fresh tenant scope per item.</summary>
public sealed class TenantJobScheduler(TenantWorkerScope scopes)
{
    public async Task RunAsync<TJob>(IEnumerable<Guid> tenantIds, int maxConcurrency,
        Func<TJob, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        ArgumentNullException.ThrowIfNull(work);
        if (maxConcurrency < 1 || maxConcurrency > 32)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        var ids = tenantIds.ToArray();
        if (ids.Any(id => id == Guid.Empty) || ids.Length != ids.Distinct().Count())
            throw new InvalidOperationException("A tenant sweep requires distinct valid tenant IDs.");

        var failures = new ConcurrentQueue<Exception>();
        await Parallel.ForEachAsync(ids, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxConcurrency,
            CancellationToken = cancellationToken
        }, async (tenantId, token) =>
        {
            try
            {
                await scopes.RunAsync<TJob, bool>(tenantId, async (job, innerToken) =>
                {
                    await work(job, innerToken);
                    return true;
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                failures.Enqueue(error);
            }
        });
        if (!failures.IsEmpty)
            throw new AggregateException("One or more tenant jobs failed.", failures);
    }
}
