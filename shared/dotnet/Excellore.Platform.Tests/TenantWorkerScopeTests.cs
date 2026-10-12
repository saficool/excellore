using Excellore.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Excellore.Platform.Tests;

public sealed class TenantWorkerScopeTests
{
    [Fact]
    public async Task EachJobGetsItsOwnTenantBoundScope()
    {
        using var provider = CreateProvider();
        var worker = provider.GetRequiredService<TenantWorkerScope>();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        var first = await worker.RunAsync<ProbeJob, (Guid TenantId, ProbeJob Job)>(t1,
            (job, _) => Task.FromResult((job.TenantId, job)));
        var second = await worker.RunAsync<ProbeJob, (Guid TenantId, ProbeJob Job)>(t2,
            (job, _) => Task.FromResult((job.TenantId, job)));

        Assert.Equal(t1, first.TenantId);
        Assert.Equal(t2, second.TenantId);
        Assert.NotSame(first.Job, second.Job);
        Assert.True(first.Job.IsDisposed);
        Assert.True(second.Job.IsDisposed);
    }

    [Fact]
    public async Task FailedJobDisposesScopeAndNextJobHasItsOwnTenant()
    {
        using var provider = CreateProvider();
        var worker = provider.GetRequiredService<TenantWorkerScope>();
        ProbeJob? failedJob = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.RunAsync<ProbeJob, bool>(Guid.NewGuid(), (job, _) =>
            {
                failedJob = job;
                throw new InvalidOperationException("Synthetic job failure.");
            }));

        Assert.NotNull(failedJob);
        Assert.True(failedJob.IsDisposed);
        var nextTenant = Guid.NewGuid();
        var observed = await worker.RunAsync<ProbeJob, Guid>(nextTenant,
            (job, _) => Task.FromResult(job.TenantId));
        Assert.Equal(nextTenant, observed);
    }

    [Fact]
    public async Task EmptyTenantAndPreCanceledWorkNeverResolveAJob()
    {
        var constructions = 0;
        var services = new ServiceCollection();
        services.AddExcelloreWorkerTenantScope();
        services.AddScoped<ProbeJob>(provider =>
        {
            constructions++;
            return new ProbeJob(provider.GetRequiredService<ITenantIdAccessor>());
        });
        using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<TenantWorkerScope>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.RunAsync<ProbeJob, Guid>(Guid.Empty, (job, _) => Task.FromResult(job.TenantId)));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            worker.RunAsync<ProbeJob, Guid>(Guid.NewGuid(),
                (job, _) => Task.FromResult(job.TenantId), canceled.Token));
        Assert.Equal(0, constructions);
    }

    [Fact]
    public async Task ReplacedTenantAccessorFailsBeforeJobResolution()
    {
        var constructions = 0;
        var services = new ServiceCollection();
        services.AddExcelloreWorkerTenantScope();
        services.AddScoped<ITenantIdAccessor>(_ => new UnboundTenant());
        services.AddScoped<ProbeJob>(provider =>
        {
            constructions++;
            return new ProbeJob(provider.GetRequiredService<ITenantIdAccessor>());
        });
        using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<TenantWorkerScope>()
                .RunAsync<ProbeJob, Guid>(Guid.NewGuid(),
                    (job, _) => Task.FromResult(job.TenantId)));
        Assert.Equal(0, constructions);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddExcelloreWorkerTenantScope();
        services.AddScoped<ProbeJob>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class ProbeJob(ITenantIdAccessor tenant) : IAsyncDisposable
    {
        // Constructor access proves the tenant is bound before job resolution.
        public Guid TenantId { get; } = tenant.TenantId;
        public bool IsDisposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnboundTenant : ITenantIdAccessor
    {
        public Guid TenantId => throw new InvalidOperationException("Unbound.");
    }
}
