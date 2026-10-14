using Excellore.Platform;
using Excellore.Sample.Domain;
using Microsoft.EntityFrameworkCore;

namespace Excellore.Sample.Infrastructure;

public sealed class SampleDbContext(DbContextOptions<SampleDbContext> options, ITenantIdAccessor tenant)
    : TenantDbContext(options, tenant)
{
    public DbSet<SampleItem> Items => Set<SampleItem>();

    protected override void ConfigureTenantModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SampleItem>(entity =>
        {
            entity.ToTable("sample_items");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.TenantId).HasColumnName("tenant_id");
            entity.Property(item => item.Code).HasColumnName("code").HasMaxLength(64);
        });
    }
}
