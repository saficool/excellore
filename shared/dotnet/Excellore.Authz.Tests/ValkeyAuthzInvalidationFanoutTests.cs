using System.Collections.Immutable;
using System.Text.Json;
using Excellore.Contracts;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Excellore.Authz.Tests;

public sealed class ValkeyAuthzInvalidationFanoutTests
{
    private const string ValkeyRepository = "valkey/valkey";
    private const string ImageRelease = "8.1.10-alpine3.24";

    [Fact]
    public async Task Event_on_one_replica_invalidates_other_replica_without_cross_tenant_leakage()
    {
        await using var container = new RedisBuilder($"{ValkeyRepository}:{ImageRelease}").Build();
        await container.StartAsync();
        using var firstConnection = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        using var secondConnection = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var changedUser = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var access = new Source();
        var firstCache = new ValkeyAuthzCache(firstConnection, "profile");
        var secondCache = new ValkeyAuthzCache(secondConnection, "profile");
        using var first = new CachedAuthzResolver(
            new ValkeyAuthzSnapshotSource(firstCache, access), TimeProvider.System);
        using var second = new CachedAuthzResolver(
            new ValkeyAuthzSnapshotSource(secondCache, access), TimeProvider.System);
        var firstFanout = new ValkeyAuthzInvalidationFanout(first, firstCache, firstConnection, "profile");
        var secondFanout = new ValkeyAuthzInvalidationFanout(second, secondCache, secondConnection, "profile");
        var handler = new AccessAuthzChangedHandler(firstFanout);
        var message = Event(tenant, [changedUser]);
        await Assert.ThrowsAsync<AuthzUnavailableException>(() => handler.HandleAsync(message));
        await firstFanout.StartAsync(CancellationToken.None);
        await secondFanout.StartAsync(CancellationToken.None);
        try
        {
            await second.ResolveAsync(changedUser, tenant, CancellationToken.None);
            await second.ResolveAsync(otherUser, tenant, CancellationToken.None);
            await second.ResolveAsync(changedUser, otherTenant, CancellationToken.None);
            await first.ResolveAsync(changedUser, tenant, CancellationToken.None);
            Assert.Equal(3, access.Calls);

            access.Deny = true;
            await handler.HandleAsync(message);
            await WaitUntilDenied(second, changedUser, tenant);
            Assert.Equal(AuthzDecision.Allow,
                (await second.ResolveAsync(otherUser, tenant, CancellationToken.None)).Decision);
            Assert.Equal(AuthzDecision.Allow,
                (await second.ResolveAsync(changedUser, otherTenant, CancellationToken.None)).Decision);
            var generation = (await secondCache.ReadAsync(changedUser, tenant)).Generation;

            await handler.HandleAsync(message);
            Assert.Equal(generation, (await secondCache.ReadAsync(changedUser, tenant)).Generation);

            await handler.HandleAsync(Event(tenant));
            await WaitUntilDenied(second, otherUser, tenant);
            Assert.Equal(AuthzDecision.Allow,
                (await second.ResolveAsync(changedUser, otherTenant, CancellationToken.None)).Decision);
        }
        finally
        {
            await firstFanout.StopAsync(CancellationToken.None);
            await secondFanout.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilDenied(CachedAuthzResolver resolver, Guid userId, Guid tenantId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            if ((await resolver.ResolveAsync(userId, tenantId, timeout.Token)).Decision == AuthzDecision.Deny)
            {
                return;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    private static byte[] Event(Guid tenantId, Guid[]? userIds = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new PlatformEvent<AccessAuthzChangedV1>
        {
            SpecVersion = "1.0",
            Id = Guid.NewGuid(),
            Type = AccessAuthzChangedV1.EventType,
            Source = AccessAuthzChangedV1.EventSource,
            Time = DateTimeOffset.UtcNow,
            TenantId = tenantId,
            CorrelationId = Guid.NewGuid(),
            Actor = new EventActor { Sub = Guid.NewGuid().ToString(), Type = "human" },
            Data = new AccessAuthzChangedV1
            {
                TenantId = tenantId,
                UserIds = userIds,
                AuthzVersion = 1
            }
        });

    private sealed class Source : IAuthzSnapshotSource
    {
        public bool Deny { get; set; }
        public int Calls { get; private set; }

        public Task<AuthzSnapshot> ResolveFreshAsync(Guid userId, Guid tenantId,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AuthzSnapshot(tenantId,
                Deny ? AuthzDecision.Deny : AuthzDecision.Allow,
                Deny ? null : "active", Deny ? null : "active",
                ImmutableHashSet<string>.Empty));
        }
    }
}
