using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Excellore.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using StackExchange.Redis;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Excellore.Authz.Tests;

public sealed class ValkeyAuthzInvalidationFanoutTests
{
    private const string ValkeyRepository = "valkey/valkey";
    private const string ImageRelease = "8.1.10-alpine3.24";
    private const string RabbitRepository = "rabbitmq";
    private const string RabbitRelease = "4.3.5-management";

    [Fact]
    public async Task Registration_uses_one_resolver_and_starts_its_invalidation_subscriber()
    {
        await using var container = new RedisBuilder($"{ValkeyRepository}:{ImageRelease}").Build();
        await container.StartAsync();
        await using var rabbit = new RabbitMqBuilder($"{RabbitRepository}:{RabbitRelease}").Build();
        await rabbit.StartAsync();
        var brokerUri = new Uri(rabbit.GetConnectionString());
        using var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Authz:AccessInternalUrl"] = new UriBuilder(Uri.UriSchemeHttp,
                    IPAddress.Loopback.ToString(), 1).Uri.ToString(),
                ["Authz:TokenEndpoint"] = new UriBuilder(Uri.UriSchemeHttps,
                    IPAddress.Loopback.ToString(), 443,
                    "/realms/test/protocol/openid-connect/token").Uri.ToString(),
                ["Authz:ClientId"] = "test-client",
                ["Authz:ClientSecret"] = Guid.NewGuid().ToString("N")
            }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(redis);
        services.AddValkeyAuthzResolver(configuration, "profile");
        services.AddRabbitMqAuthzChangedConsumer("profile", brokerUri);
        services.AddCachedAuthzResolver();
        services.AddAccessAuthzClient(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var resolver = provider.GetRequiredService<IAuthzResolver>();
        var cache = provider.GetRequiredService<ValkeyAuthzCache>();
        var fanout = provider.GetRequiredService<ValkeyAuthzInvalidationFanout>();
        Assert.Same(fanout, provider.GetRequiredService<IAuthzCacheInvalidator>());
        var hosted = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(2, hosted.Length);
        Assert.Same(fanout, hosted[0]);
        Assert.IsType<RabbitMqAuthzChangedConsumer>(hosted[1]);
        Assert.IsType<ValkeyAuthzSnapshotSource>(provider.GetRequiredService<IAuthzSnapshotSource>());
        Assert.Same(cache, provider.GetRequiredService<IAuthzDistributedSnapshotCache>());
        Assert.Same(resolver, provider.GetRequiredService<CachedAuthzResolver>());

        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var initial = await cache.ReadAsync(user, tenant);
        Assert.True(await cache.StoreIfCurrentAsync(user, tenant,
            Allowed(tenant), initial.Generation));
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }
        try
        {
            Assert.Equal(AuthzDecision.Allow,
                (await resolver.ResolveAsync(user, tenant, CancellationToken.None)).Decision);
            await using (var connection = await new ConnectionFactory { Uri = brokerUri }
                             .CreateConnectionAsync())
            await using (var channel = await connection.CreateChannelAsync())
            {
                await channel.BasicPublishAsync("excellore.events", AccessAuthzChangedV1.EventType,
                    mandatory: false, new BasicProperties { Persistent = true }, Event(tenant, [user]));
            }

            ValkeyAuthzRead refreshed;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            do
            {
                refreshed = await cache.ReadAsync(user, tenant, timeout.Token);
                if (refreshed.Snapshot is not null) await Task.Delay(20, timeout.Token);
            } while (refreshed.Snapshot is not null);
            Assert.Null(refreshed.Snapshot);
            Assert.True(await cache.StoreIfCurrentAsync(user, tenant,
                Denied(tenant), refreshed.Generation));
            Assert.Equal(AuthzDecision.Deny,
                (await resolver.ResolveAsync(user, tenant, CancellationToken.None)).Decision);
        }
        finally
        {
            foreach (var service in hosted.Reverse())
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }

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

    private static AuthzSnapshot Allowed(Guid tenantId) =>
        new(tenantId, AuthzDecision.Allow, "active", "active", ImmutableHashSet<string>.Empty);

    private static AuthzSnapshot Denied(Guid tenantId) =>
        new(tenantId, AuthzDecision.Deny, null, null, ImmutableHashSet<string>.Empty);

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
