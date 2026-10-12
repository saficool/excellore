using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Excellore.Platform;

/// <summary>A mapped class T row with a required tenant discriminator.</summary>
public interface ITenantOwnedEntity
{
    Guid TenantId { get; set; }
}

/// <summary>EF context for class T tables; platform and hybrid tables use separate contexts.</summary>
public abstract class TenantDbContext(DbContextOptions options, ITenantIdAccessor tenant) : DbContext(options)
{
    public Guid TenantId => tenant.TenantId;

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureTenantModel(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToArray())
        {
            if (entityType.IsOwned())
            {
                throw new InvalidOperationException("Tenant model contains an unsupported entity or query filter.");
            }

            if (!typeof(ITenantOwnedEntity).IsAssignableFrom(entityType.ClrType) ||
                entityType.BaseType is not null ||
                entityType.FindProperty(nameof(ITenantOwnedEntity.TenantId)) is not { ClrType: var tenantType } ||
                tenantType != typeof(Guid) || entityType.GetDeclaredQueryFilters().Any())
            {
                throw new InvalidOperationException("Tenant model contains an unsupported entity or query filter.");
            }

            var parameter = Expression.Parameter(entityType.ClrType, "entity");
            var property = Expression.Call(typeof(EF).GetMethod(nameof(EF.Property))!
                    .MakeGenericMethod(typeof(Guid)), parameter,
                Expression.Constant(nameof(ITenantOwnedEntity.TenantId)));
            var currentTenant = Expression.Property(Expression.Constant(this), nameof(TenantId));
            var filter = Expression.Lambda(Expression.Equal(property, currentTenant), parameter);
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(filter);
        }
    }

    protected virtual void ConfigureTenantModel(ModelBuilder modelBuilder) { }

    public override int SaveChanges() => SaveChanges(true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAndValidate();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(true, cancellationToken);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampAndValidate();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampAndValidate()
    {
        var tenantId = TenantId;
        if (tenantId == Guid.Empty)
        {
            throw new InvalidOperationException("Tenant context is required for database work.");
        }

        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<ITenantOwnedEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = tenantId;
            }

            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
                (entry.Entity.TenantId != tenantId ||
                 (entry.State != EntityState.Added &&
                  entry.Property(nameof(ITenantOwnedEntity.TenantId)).OriginalValue is Guid original &&
                  original != tenantId)))
            {
                throw new InvalidOperationException("Tenant entity does not belong to the active tenant.");
            }
        }
    }
}
