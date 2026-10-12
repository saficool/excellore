using Microsoft.EntityFrameworkCore;

namespace Excellore.Platform;

/// <summary>Runs tenant work inside a transaction with PostgreSQL RLS context set first.</summary>
public sealed class TenantUnitOfWork<TDbContext>(TDbContext db, ITenantIdAccessor tenant)
    where TDbContext : DbContext
{
    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var tenantId = tenant.TenantId;
        if (tenantId == Guid.Empty)
        {
            throw new InvalidOperationException("Tenant context is required for database work.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)", cancellationToken);

        var result = await work();
        if (tenant.TenantId != tenantId)
        {
            throw new InvalidOperationException("Tenant context changed during database work.");
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
