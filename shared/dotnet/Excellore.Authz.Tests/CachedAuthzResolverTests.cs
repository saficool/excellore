using System.Collections.Immutable;
using Excellore.Authz;
using Microsoft.Extensions.DependencyInjection;

namespace Excellore.Authz.Tests;

public class CachedAuthzResolverTests
{
    private static readonly Guid TenantOne = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid TenantTwo = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid UserOne = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid UserTwo = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Fresh_snapshot_is_cached_for_ten_seconds_per_user_and_tenant()
    {
        var clock = new ManualClock();
        var source = new StubSource((_, tenant) => Task.FromResult(Allowed(tenant)));
        using var resolver = new CachedAuthzResolver(source, clock);

        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(9));
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        Assert.Equal(1, source.CallCount);

        await resolver.ResolveAsync(UserTwo, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserOne, TenantTwo, CancellationToken.None);
        Assert.Equal(3, source.CallCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        Assert.Equal(4, source.CallCount);
    }

    [Fact]
    public async Task Denials_are_cached_but_expire()
    {
        var clock = new ManualClock();
        var denied = new AuthzSnapshot(TenantOne, AuthzDecision.Deny, null, null,
            ImmutableHashSet<string>.Empty);
        var source = new StubSource((_, _) => Task.FromResult(denied));
        using var resolver = new CachedAuthzResolver(source, clock);

        Assert.Equal(AuthzDecision.Deny,
            (await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None)).Decision);
        Assert.Equal(AuthzDecision.Deny,
            (await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None)).Decision);
        Assert.Equal(1, source.CallCount);

        clock.Advance(TimeSpan.FromSeconds(10));
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task Targeted_and_tenant_wide_invalidation_remove_only_matching_entries()
    {
        var source = new StubSource((_, tenant) => Task.FromResult(Allowed(tenant)));
        using var resolver = new CachedAuthzResolver(source, new ManualClock());
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserTwo, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserOne, TenantTwo, CancellationToken.None);
        Assert.Equal(3, source.CallCount);

        resolver.Invalidate(TenantOne, [UserOne]);
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserTwo, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserOne, TenantTwo, CancellationToken.None);
        Assert.Equal(4, source.CallCount);

        resolver.Invalidate(TenantOne);
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserTwo, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserOne, TenantTwo, CancellationToken.None);
        Assert.Equal(6, source.CallCount);
    }

    [Fact]
    public async Task Empty_user_list_invalidates_the_entire_tenant_and_repeating_is_safe()
    {
        var source = new StubSource((_, tenant) => Task.FromResult(Allowed(tenant)));
        using var resolver = new CachedAuthzResolver(source, new ManualClock());
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);

        resolver.Invalidate(TenantOne, []);
        resolver.Invalidate(TenantOne, []);
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);

        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task Invalid_user_id_in_an_invalidation_clears_the_entire_tenant()
    {
        var source = new StubSource((_, tenant) => Task.FromResult(Allowed(tenant)));
        using var resolver = new CachedAuthzResolver(source, new ManualClock());
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserTwo, TenantOne, CancellationToken.None);

        resolver.Invalidate(TenantOne, [Guid.Empty]);
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserTwo, TenantOne, CancellationToken.None);

        Assert.Equal(4, source.CallCount);
    }

    [Fact]
    public async Task Expired_snapshot_is_not_served_when_authority_is_unavailable()
    {
        var clock = new ManualClock();
        var unavailable = false;
        var source = new StubSource((_, tenant) => unavailable
            ? Task.FromException<AuthzSnapshot>(new AuthzUnavailableException())
            : Task.FromResult(Allowed(tenant)));
        using var resolver = new CachedAuthzResolver(source, clock);
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(10));
        unavailable = true;

        await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
            resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None));
    }

    [Fact]
    public async Task Invalidation_during_fetch_discards_the_old_result()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSnapshot = Allowed(TenantOne, "profile.member.read");
        var newSnapshot = Allowed(TenantOne);
        var calls = 0;
        var source = new StubSource(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult();
                await release.Task;
                return oldSnapshot;
            }

            return newSnapshot;
        });
        using var resolver = new CachedAuthzResolver(source, new ManualClock());

        var resolving = resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        resolver.Invalidate(TenantOne, [UserOne]);
        release.SetResult();

        Assert.Same(newSnapshot, await resolving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(newSnapshot, await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None));
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_key_share_one_authority_call()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new StubSource(async (_, tenant) =>
        {
            started.SetResult();
            await release.Task;
            return Allowed(tenant);
        });
        using var resolver = new CachedAuthzResolver(source, new ManualClock());

        var requests = Enumerable.Range(0, 20)
            .Select(_ => resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None)).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task Wrong_tenant_response_is_rejected_and_not_cached()
    {
        var source = new StubSource((_, _) => Task.FromResult(Allowed(TenantTwo)));
        using var resolver = new CachedAuthzResolver(source, new ManualClock());

        await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
            resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None));
        await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
            resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None));
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public void Registration_exposes_one_cache_instance_for_resolve_and_invalidation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthzSnapshotSource>(new StubSource((_, tenant) => Task.FromResult(Allowed(tenant))));
        services.AddCachedAuthzResolver();
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IAuthzResolver>(),
            provider.GetRequiredService<IAuthzCacheInvalidator>());
    }

    private static AuthzSnapshot Allowed(Guid tenantId, params string[] permissions) =>
        new(tenantId, AuthzDecision.Allow, "active", "active",
            ImmutableHashSet.CreateRange(StringComparer.Ordinal, permissions));

    private sealed class StubSource(Func<Guid, Guid, Task<AuthzSnapshot>> resolve) : IAuthzSnapshotSource
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<AuthzSnapshot> ResolveFreshAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return resolve(userId, tenantId);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
