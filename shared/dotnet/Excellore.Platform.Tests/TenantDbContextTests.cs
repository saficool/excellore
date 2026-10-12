using Excellore.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Excellore.Platform.Tests;

public class TenantDbContextTests
{
    [Fact]
    public async Task Query_filter_uses_each_contexts_tenant_and_adds_tenant_stamp()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();
        await using var first = new NotesContext(options, new StubTenant(tenantOne));
        await first.Database.EnsureCreatedAsync();
        first.Notes.Add(new Note { Text = "one" });
        await first.SaveChangesAsync();
        Assert.Equal(tenantOne, (await first.Notes.SingleAsync()).TenantId);

        await using var second = new NotesContext(options, new StubTenant(tenantTwo));
        second.Notes.Add(new Note { Text = "two" });
        await second.SaveChangesAsync();

        Assert.Equal(["one"], await first.Notes.AsNoTracking().Select(note => note.Text).ToArrayAsync());
        Assert.Equal(["two"], await second.Notes.AsNoTracking().Select(note => note.Text).ToArrayAsync());
        Assert.NotEmpty(first.Model.FindEntityType(typeof(Note))!.GetDeclaredQueryFilters());
    }

    [Fact]
    public async Task Explicit_cross_tenant_add_update_and_delete_are_rejected()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();
        await using var db = new NotesContext(options, new StubTenant(tenantOne));
        await db.Database.EnsureCreatedAsync();

        db.Notes.Add(new Note { Text = "wrong", TenantId = tenantTwo });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var row = new Note { Text = "one" };
        db.Notes.Add(row);
        await db.SaveChangesAsync();
        row.TenantId = tenantTwo;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var foreign = new Note { Id = row.Id, Text = "one", TenantId = tenantTwo };
        db.Notes.Attach(foreign);
        db.Notes.Remove(foreign);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var changedFromForeign = new Note { Id = row.Id, Text = "one", TenantId = tenantTwo };
        db.Notes.Attach(changedFromForeign);
        changedFromForeign.TenantId = tenantOne;
        db.Entry(changedFromForeign).State = EntityState.Modified;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Notes.CountAsync());
    }

    [Fact]
    public async Task Synchronous_save_cannot_bypass_tenant_validation()
    {
        await using var connection = await OpenDatabaseAsync();
        var tenant = Guid.NewGuid();
        await using var db = new NotesContext(Options(connection), new StubTenant(tenant));
        await db.Database.EnsureCreatedAsync();
        db.Notes.Add(new Note { Text = "wrong", TenantId = Guid.NewGuid() });
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task Missing_tenant_and_non_tenant_model_fail_closed()
    {
        await using var connection = await OpenDatabaseAsync();
        var tenant = new StubTenant(Guid.NewGuid());
        await using var db = new NotesContext(Options(connection), tenant);
        await db.Database.EnsureCreatedAsync();
        tenant.TenantId = Guid.Empty;
        db.Notes.Add(new Note { Text = "not saved" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal(0, await db.Notes.CountAsync());

        await using var invalid = new InvalidContext(
            new DbContextOptionsBuilder<InvalidContext>().UseSqlite(connection).Options,
            new StubTenant(Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => _ = invalid.Model);

        await using var filtered = new FilteredContext(
            new DbContextOptionsBuilder<FilteredContext>().UseSqlite(connection).Options,
            new StubTenant(Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => _ = filtered.Model);
    }

    private static DbContextOptions<NotesContext> Options(SqliteConnection connection) =>
        new DbContextOptionsBuilder<NotesContext>().UseSqlite(connection).Options;

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private sealed class StubTenant(Guid tenantId) : ITenantIdAccessor
    {
        public Guid TenantId { get; set; } = tenantId;
    }

    private sealed class Note : ITenantOwnedEntity
    {
        public int Id { get; set; }
        public Guid TenantId { get; set; }
        public string Text { get; set; } = "";
    }

    private sealed class PlatformNote
    {
        public int Id { get; set; }
    }

    private sealed class NotesContext(DbContextOptions<NotesContext> options, ITenantIdAccessor tenant)
        : TenantDbContext(options, tenant)
    {
        public DbSet<Note> Notes => Set<Note>();
    }

    private sealed class InvalidContext(DbContextOptions<InvalidContext> options, ITenantIdAccessor tenant)
        : TenantDbContext(options, tenant)
    {
        public DbSet<PlatformNote> Notes => Set<PlatformNote>();
    }

    private sealed class FilteredContext(DbContextOptions<FilteredContext> options, ITenantIdAccessor tenant)
        : TenantDbContext(options, tenant)
    {
        public DbSet<Note> Notes => Set<Note>();

        protected override void ConfigureTenantModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Note>().HasQueryFilter(note => note.Text != "hidden");
    }
}
