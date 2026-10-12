using Microsoft.Extensions.DependencyInjection;

namespace Excellore.Platform;

/// <summary>Opens a fresh tenant-bound service scope for one background job.</summary>
public sealed class TenantWorkerScope(IServiceScopeFactory scopeFactory)
{
    public async Task<TResult> RunAsync<TJob, TResult>(
        Guid tenantId,
        Func<TJob, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(work);
        if (tenantId == Guid.Empty)
        {
            throw new InvalidOperationException("Tenant context is required for worker database work.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var scope = scopeFactory.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<WorkerTenantIdAccessor>();
        if (!ReferenceEquals(tenant, scope.ServiceProvider.GetRequiredService<ITenantIdAccessor>()))
        {
            throw new InvalidOperationException("Worker tenant context registration was replaced.");
        }

        tenant.Open(tenantId);
        var job = scope.ServiceProvider.GetRequiredService<TJob>();
        return await work(job, cancellationToken);
    }
}

public static class TenantWorkerScopeServiceCollectionExtensions
{
    public static IServiceCollection AddExcelloreWorkerTenantScope(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<WorkerTenantIdAccessor>();
        services.AddScoped<ITenantIdAccessor>(provider =>
            provider.GetRequiredService<WorkerTenantIdAccessor>());
        services.AddSingleton<TenantWorkerScope>();
        return services;
    }
}

internal sealed class WorkerTenantIdAccessor : ITenantIdAccessor
{
    private Guid _tenantId;

    public Guid TenantId => _tenantId != Guid.Empty
        ? _tenantId
        : throw new InvalidOperationException("Tenant context is required for worker database work.");

    internal void Open(Guid tenantId)
    {
        if (tenantId == Guid.Empty || _tenantId != Guid.Empty)
        {
            throw new InvalidOperationException("Worker tenant context is already bound or invalid.");
        }

        _tenantId = tenantId;
    }
}
