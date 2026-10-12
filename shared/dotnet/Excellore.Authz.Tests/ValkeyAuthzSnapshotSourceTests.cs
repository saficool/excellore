using System.Collections.Immutable;
using Excellore.Authz;

namespace Excellore.Authz.Tests;

public sealed class ValkeyAuthzSnapshotSourceTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid User = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    [Fact]
    public async Task DistributedHitAvoidsAccessAndRejectsCrossTenantValue()
    {
        var cache = new StubCache { Read = () => new ValkeyAuthzRead(Allowed(Tenant), default) };
        var access = new StubSource(() => throw new InvalidOperationException("Access should not be called."));
        var source = new ValkeyAuthzSnapshotSource(cache, access);

        Assert.Equal(Tenant, (await source.ResolveFreshAsync(User, Tenant, CancellationToken.None)).TenantId);
        Assert.Equal(0, access.Calls);

        cache.Read = () => new ValkeyAuthzRead(Allowed(OtherTenant), default);
        await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
            source.ResolveFreshAsync(User, Tenant, CancellationToken.None));
        Assert.Equal(0, access.Calls);
    }

    [Fact]
    public async Task InvalidationDuringAccessFetchDiscardsTheOldDecision()
    {
        var cache = new StubCache
        {
            Read = () => new ValkeyAuthzRead(null, default),
            Store = call => call == 2
        };
        var access = new StubSource(() =>
            Task.FromResult(cache.StoreCalls == 0
                ? Allowed(Tenant, "access.role.manage")
                : Allowed(Tenant)));
        var source = new ValkeyAuthzSnapshotSource(cache, access);

        var result = await source.ResolveFreshAsync(User, Tenant, CancellationToken.None);

        Assert.Empty(result.Permissions);
        Assert.Equal(2, access.Calls);
        Assert.Equal(2, cache.StoreCalls);
    }

    [Fact]
    public async Task ValkeyReadFailureUsesFreshAccessDecision()
    {
        var cache = new StubCache { Read = () => throw new AuthzUnavailableException() };
        var access = new StubSource(() => Task.FromResult(Allowed(Tenant)));
        var source = new ValkeyAuthzSnapshotSource(cache, access);

        Assert.Equal(Tenant, (await source.ResolveFreshAsync(User, Tenant, CancellationToken.None)).TenantId);
        Assert.Equal(1, access.Calls);
        Assert.Equal(0, cache.StoreCalls);

        access.Resolve = () => throw new AuthzUnavailableException();
        await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
            source.ResolveFreshAsync(User, Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task ValkeyWriteFailureReturnsOnlyTheFreshAccessDecision()
    {
        var cache = new StubCache
        {
            Read = () => new ValkeyAuthzRead(null, default),
            Store = _ => throw new AuthzUnavailableException()
        };
        var source = new ValkeyAuthzSnapshotSource(cache,
            new StubSource(() => Task.FromResult(Allowed(Tenant))));

        Assert.Equal(Tenant, (await source.ResolveFreshAsync(User, Tenant, CancellationToken.None)).TenantId);
        Assert.Equal(1, cache.StoreCalls);
    }

    [Fact]
    public async Task WrongTenantFromAccessAndRepeatedInvalidationFailClosed()
    {
        var cache = new StubCache
        {
            Read = () => new ValkeyAuthzRead(null, default),
            Store = _ => false
        };
        var access = new StubSource(() => Task.FromResult(Allowed(OtherTenant)));
        var source = new ValkeyAuthzSnapshotSource(cache, access);

        await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
            source.ResolveFreshAsync(User, Tenant, CancellationToken.None));
        Assert.Equal(0, cache.StoreCalls);

        access.Resolve = () => Task.FromResult(Allowed(Tenant));
        await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
            source.ResolveFreshAsync(User, Tenant, CancellationToken.None));
        Assert.Equal(3, cache.StoreCalls);
    }

    private static AuthzSnapshot Allowed(Guid tenant, params string[] permissions) =>
        new(tenant, AuthzDecision.Allow, "active", "active",
            ImmutableHashSet.CreateRange(StringComparer.Ordinal, permissions));

    private sealed class StubCache : IAuthzDistributedSnapshotCache
    {
        public Func<ValkeyAuthzRead> Read { get; set; } = () => new ValkeyAuthzRead(null, default);
        public Func<int, bool> Store { get; set; } = _ => true;
        public int StoreCalls { get; private set; }

        public Task<ValkeyAuthzRead> ReadAsync(Guid userId, Guid tenantId,
            CancellationToken cancellationToken = default) => Task.FromResult(Read());

        public Task<bool> StoreIfCurrentAsync(Guid userId, Guid tenantId, AuthzSnapshot snapshot,
            AuthzCacheGeneration generation, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store(++StoreCalls));

        public Task InvalidateAsync(Guid tenantId, IReadOnlyCollection<Guid>? userIds = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubSource(Func<Task<AuthzSnapshot>> resolve) : IAuthzSnapshotSource
    {
        public Func<Task<AuthzSnapshot>> Resolve { get; set; } = resolve;
        public int Calls { get; private set; }
        public Task<AuthzSnapshot> ResolveFreshAsync(Guid userId, Guid tenantId,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Resolve();
        }
    }
}
