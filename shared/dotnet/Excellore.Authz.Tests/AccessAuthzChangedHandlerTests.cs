using System.Collections.Immutable;
using System.Text.Json;
using Excellore.Authz;
using Excellore.Contracts;

namespace Excellore.Authz.Tests;

public class AccessAuthzChangedHandlerTests
{
    private static readonly Guid TenantOne = Guid.NewGuid();
    private static readonly Guid TenantTwo = Guid.NewGuid();
    private static readonly Guid UserOne = Guid.NewGuid();
    private static readonly Guid UserTwo = Guid.NewGuid();

    [Fact]
    public async Task Targeted_invalidation_is_tenant_scoped_and_duplicate_delivery_is_idempotent()
    {
        var source = new Source();
        using var resolver = new CachedAuthzResolver(source, TimeProvider.System);
        var handler = new AccessAuthzChangedHandler(resolver);
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserTwo, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserOne, TenantTwo, CancellationToken.None);
        Assert.Equal(3, source.Calls);

        var message = Event(TenantOne, [UserOne]);
        await handler.HandleAsync(message);
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserTwo, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserOne, TenantTwo, CancellationToken.None);
        Assert.Equal(4, source.Calls);

        await handler.HandleAsync(message);
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        Assert.Equal(4, source.Calls);
    }

    [Fact]
    public async Task Missing_user_ids_clear_whole_tenant_only()
    {
        var source = new Source();
        using var resolver = new CachedAuthzResolver(source, TimeProvider.System);
        var handler = new AccessAuthzChangedHandler(resolver);
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserTwo, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserOne, TenantTwo, CancellationToken.None);

        await handler.HandleAsync(Event(TenantOne));
        await resolver.ResolveAsync(UserOne, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserTwo, TenantOne, CancellationToken.None);
        await resolver.ResolveAsync(UserOne, TenantTwo, CancellationToken.None);

        Assert.Equal(5, source.Calls);
    }

    [Fact]
    public async Task Malformed_or_cross_tenant_events_do_not_invalidate()
    {
        var invalidator = new CountingInvalidator();
        var handler = new AccessAuthzChangedHandler(invalidator);
        var message = JsonSerializer.Deserialize<PlatformEvent<AccessAuthzChangedV1>>(Event(TenantOne))!;
        foreach (var broken in new[]
        {
            message with { Type = "access.authz.changed.v2" },
            message with { TenantId = TenantTwo },
            message with { Data = message.Data! with { AuthzVersion = 0 } },
            message with { Id = Guid.Empty },
            message with { Actor = null }
        })
        {
            var error = await Assert.ThrowsAsync<InvalidPlatformEventException>(() =>
                handler.HandleAsync(JsonSerializer.SerializeToUtf8Bytes(broken)));
            Assert.Equal("Invalid platform event.", error.Message);
        }

        await Assert.ThrowsAsync<InvalidPlatformEventException>(() =>
            handler.HandleAsync("not json"u8.ToArray()));
        Assert.Equal(0, invalidator.Calls);
    }

    [Fact]
    public async Task Failed_invalidation_is_retried_on_duplicate_delivery()
    {
        var invalidator = new CountingInvalidator { FailOnce = true };
        var handler = new AccessAuthzChangedHandler(invalidator);
        var message = Event(TenantOne, [UserOne]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(message));
        await handler.HandleAsync(message);
        await handler.HandleAsync(message);

        Assert.Equal(2, invalidator.Calls);
    }

    [Fact]
    public void Json_schema_is_packaged_with_required_envelope_and_data_fields()
    {
        using var stream = typeof(AccessAuthzChangedV1).Assembly.GetManifestResourceStream(
            "Excellore.Contracts.Schemas.access.authz.changed.v1.schema.json");
        Assert.NotNull(stream);
        using var schema = JsonDocument.Parse(stream);
        var root = schema.RootElement;
        Assert.Equal(AccessAuthzChangedV1.EventType, root.GetProperty("title").GetString());
        var required = root.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).ToHashSet();
        Assert.Contains("tenantid", required);
        Assert.Contains("data", required);
        var dataRequired = root.GetProperty("properties").GetProperty("data")
            .GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToHashSet();
        Assert.Contains("tenantId", dataRequired);
        Assert.Contains("authzVersion", dataRequired);
    }

    [Fact]
    public void Unknown_fields_are_ignored_for_forward_compatibility()
    {
        using var document = JsonDocument.Parse(Event(TenantOne, [UserOne]));
        var root = document.RootElement;
        var properties = root.EnumerateObject().ToDictionary(item => item.Name, item => item.Value);
        properties["futureField"] = JsonDocument.Parse("true").RootElement.Clone();
        var parsed = AccessAuthzChangedV1.Parse(JsonSerializer.SerializeToUtf8Bytes(properties));
        Assert.Equal(TenantOne, parsed.TenantId);
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
        public int Calls { get; private set; }

        public Task<AuthzSnapshot> ResolveFreshAsync(Guid userId, Guid tenantId,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AuthzSnapshot(tenantId, AuthzDecision.Allow, "active", "active",
                ImmutableHashSet<string>.Empty));
        }
    }

    private sealed class CountingInvalidator : IAuthzCacheInvalidator
    {
        public int Calls { get; private set; }
        public bool FailOnce { get; init; }

        public Task InvalidateAsync(Guid tenantId, IReadOnlyCollection<Guid>? userIds = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (FailOnce && Calls == 1)
            {
                throw new InvalidOperationException("Synthetic failure.");
            }

            return Task.CompletedTask;
        }
    }
}
