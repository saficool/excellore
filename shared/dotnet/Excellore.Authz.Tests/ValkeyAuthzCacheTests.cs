using System.Collections.Immutable;
using Excellore.Authz;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Excellore.Authz.Tests;

public sealed class ValkeyAuthzCacheTests
{
    private const string ValkeyRepository = "valkey/valkey";
    private const string ImageRelease = "8.1.10-alpine3.24";

    [Fact]
    public async Task DistributedSnapshotsExpireAndInvalidateWithoutResurrectingOldDecisions()
    {
        await using var container = new RedisBuilder($"{ValkeyRepository}:{ImageRelease}").Build();
        await container.StartAsync();
        using var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        Assert.Throws<ArgumentException>(() => new ValkeyAuthzCache(redis, "Profile:other"));
        var first = new ValkeyAuthzCache(redis, "profile");
        var second = new ValkeyAuthzCache(redis, "profile");
        var otherApp = new ValkeyAuthzCache(redis, "access");
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var old = Allowed(t1, "profile.member.read");

        var initial = await first.ReadAsync(u1, t1);
        Assert.Null(initial.Snapshot);
        Assert.Equal(new AuthzCacheGeneration(0, 0), initial.Generation);
        Assert.True(await first.StoreIfCurrentAsync(u1, t1, old, initial.Generation));
        Assert.Contains("profile.member.read", (await second.ReadAsync(u1, t1)).Snapshot!.Permissions);
        Assert.Null((await otherApp.ReadAsync(u1, t1)).Snapshot);

        var key = $"profile:authz:{t1:D}:{u1:D}";
        var ttl = await redis.GetDatabase().KeyTimeToLiveAsync(key);
        Assert.InRange(ttl!.Value.TotalSeconds, 1, 60);

        var u2Read = await first.ReadAsync(u2, t1);
        Assert.True(await first.StoreIfCurrentAsync(u2, t1, Allowed(t1), u2Read.Generation));
        var t2Read = await first.ReadAsync(u1, t2);
        Assert.True(await first.StoreIfCurrentAsync(u1, t2, Allowed(t2), t2Read.Generation));

        await second.InvalidateAsync(t1, [u1]);
        Assert.Null((await first.ReadAsync(u1, t1)).Snapshot);
        Assert.NotNull((await first.ReadAsync(u2, t1)).Snapshot);
        Assert.NotNull((await first.ReadAsync(u1, t2)).Snapshot);
        Assert.False(await first.StoreIfCurrentAsync(u1, t1, old, initial.Generation));

        var refreshed = await first.ReadAsync(u1, t1);
        Assert.True(await first.StoreIfCurrentAsync(u1, t1, Allowed(t1), refreshed.Generation));
        Assert.Empty((await second.ReadAsync(u1, t1)).Snapshot!.Permissions);

        await second.InvalidateAsync(t1);
        Assert.Null((await first.ReadAsync(u1, t1)).Snapshot);
        Assert.Null((await first.ReadAsync(u2, t1)).Snapshot);
        Assert.NotNull((await first.ReadAsync(u1, t2)).Snapshot);
        Assert.False(await first.StoreIfCurrentAsync(u2, t1, Allowed(t1), u2Read.Generation));

        await redis.GetDatabase().StringSetAsync(key, "malformed", TimeSpan.FromSeconds(60));
        Assert.Null((await first.ReadAsync(u1, t1)).Snapshot);

        var access = new StubAccess(() => Task.FromResult(Allowed(t1, "profile.member.read")));
        var resolving = new ValkeyAuthzSnapshotSource(first, access);
        await resolving.ResolveFreshAsync(u1, t1, CancellationToken.None);
        Assert.Equal(1, access.Calls);
        var replica = new ValkeyAuthzSnapshotSource(second,
            new StubAccess(() => throw new AuthzUnavailableException()));
        Assert.Contains("profile.member.read",
            (await replica.ResolveFreshAsync(u1, t1, CancellationToken.None)).Permissions);

        await first.InvalidateAsync(t1, [u1]);
        await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
            replica.ResolveFreshAsync(u1, t1, CancellationToken.None));

        redis.Dispose();
        var fallback = new ValkeyAuthzSnapshotSource(first,
            new StubAccess(() => Task.FromResult(Allowed(t1))));
        Assert.Empty((await fallback.ResolveFreshAsync(u1, t1, CancellationToken.None)).Permissions);
    }

    private static AuthzSnapshot Allowed(Guid tenantId, params string[] permissions) =>
        new(tenantId, AuthzDecision.Allow, "active", "active",
            ImmutableHashSet.CreateRange(StringComparer.Ordinal, permissions));

    private sealed class StubAccess(Func<Task<AuthzSnapshot>> resolve) : IAuthzSnapshotSource
    {
        public int Calls { get; private set; }
        public Task<AuthzSnapshot> ResolveFreshAsync(Guid userId, Guid tenantId,
            CancellationToken cancellationToken)
        {
            Calls++;
            return resolve();
        }
    }
}
