using Excellore.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Excellore.Platform.Tests;

public class TenantUnitOfWorkTests
{
    [Fact]
    public async Task Sets_tenant_transaction_locally_before_work_and_commits_changes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tenantId = Guid.NewGuid();
        var tenant = new StubTenant(tenantId);
        var unit = new TenantUnitOfWork<TestDbContext>(fixture.Db, tenant);

        var result = await unit.RunAsync(async () =>
        {
            fixture.Steps.Add("work");
            fixture.Db.Notes.Add(new Note { Text = "created" });
            return await Task.FromResult("done");
        }, CancellationToken.None);

        Assert.Equal("done", result);
        Assert.Equal(["set", "work"], fixture.Steps);
        Assert.Equal("app.tenant_id", fixture.SettingName);
        Assert.Equal(tenantId.ToString(), fixture.SettingValue);
        Assert.True(fixture.IsTransactionLocal);
        Assert.Equal(1, await fixture.Db.Notes.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Failed_work_rolls_back_even_if_work_saved_early()
    {
        await using var fixture = await Fixture.CreateAsync();
        var unit = new TenantUnitOfWork<TestDbContext>(fixture.Db, new StubTenant(Guid.NewGuid()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.RunAsync<int>(async () =>
        {
            fixture.Db.Notes.Add(new Note { Text = "rolled back" });
            await fixture.Db.SaveChangesAsync();
            throw new InvalidOperationException("Synthetic failure.");
        }, CancellationToken.None));

        Assert.Equal(0, await fixture.Db.Notes.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Missing_or_changed_tenant_context_fails_closed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tenant = new StubTenant(Guid.Empty);
        var unit = new TenantUnitOfWork<TestDbContext>(fixture.Db, tenant);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unit.RunAsync(() => Task.FromResult(1), CancellationToken.None));
        Assert.Empty(fixture.Steps);

        tenant.TenantId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.RunAsync(async () =>
        {
            fixture.Db.Notes.Add(new Note { Text = "not committed" });
            await fixture.Db.SaveChangesAsync();
            tenant.TenantId = Guid.NewGuid();
            return 1;
        }, CancellationToken.None));
        Assert.Equal(0, await fixture.Db.Notes.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Each_transaction_receives_its_own_tenant_id()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tenant = new StubTenant(Guid.NewGuid());
        var unit = new TenantUnitOfWork<TestDbContext>(fixture.Db, tenant);

        await unit.RunAsync(() => Task.FromResult(1), CancellationToken.None);
        Assert.Equal(tenant.TenantId.ToString(), fixture.SettingValue);
        tenant.TenantId = Guid.NewGuid();
        await unit.RunAsync(() => Task.FromResult(2), CancellationToken.None);
        Assert.Equal(tenant.TenantId.ToString(), fixture.SettingValue);
        Assert.Equal(["set", "set"], fixture.Steps);
    }

    private sealed class StubTenant(Guid tenantId) : ITenantIdAccessor
    {
        public Guid TenantId { get; set; } = tenantId;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<Note> Notes => Set<Note>();
    }

    private sealed class Note
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public TestDbContext Db { get; }
        public List<string> Steps { get; } = [];
        public string? SettingName { get; private set; }
        public string? SettingValue { get; private set; }
        public bool IsTransactionLocal { get; private set; }

        private Fixture(SqliteConnection connection, TestDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connection).Options;
            var db = new TestDbContext(options);
            var fixture = new Fixture(connection, db);
            connection.CreateFunction<string, string, bool, string>("set_config", (name, value, local) =>
            {
                fixture.Steps.Add("set");
                fixture.SettingName = name;
                fixture.SettingValue = value;
                fixture.IsTransactionLocal = local;
                return value;
            });
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
