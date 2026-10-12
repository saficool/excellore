using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Excellore.Authz;

public interface IAuthzSnapshotSource
{
    Task<AuthzSnapshot> ResolveFreshAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);
}

public interface IAuthzCacheInvalidator
{
    Task InvalidateAsync(Guid tenantId, IReadOnlyCollection<Guid>? userIds = null,
        CancellationToken cancellationToken = default);
}

public sealed class CachedAuthzResolver : IAuthzResolver, IAuthzCacheInvalidator, IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);
    private const int MaxEntries = 10_000;
    private readonly IAuthzSnapshotSource _source;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly Dictionary<CacheKey, CacheEntry> _entries = [];
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private long _generation;

    public CachedAuthzResolver(IAuthzSnapshotSource source, TimeProvider clock)
    {
        _source = source;
        _clock = clock;
    }

    public async Task<AuthzSnapshot> ResolveAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("A user and tenant are required to resolve authorization.");
        }

        var key = new CacheKey(tenantId, userId);
        if (TryRead(key, out var cached))
        {
            return cached;
        }

        var stripe = _stripes[(uint)key.GetHashCode() % _stripes.Length];
        await stripe.WaitAsync(cancellationToken);
        try
        {
            if (TryRead(key, out cached))
            {
                return cached;
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                long generation;
                lock (_sync)
                {
                    generation = _generation;
                }

                var snapshot = await _source.ResolveFreshAsync(userId, tenantId, cancellationToken);
                if (snapshot is null || snapshot.TenantId != tenantId || snapshot.Permissions is null)
                {
                    throw new AuthzUnavailableException();
                }

                lock (_sync)
                {
                    if (generation != _generation)
                    {
                        continue;
                    }

                    if (_entries.Count >= MaxEntries)
                    {
                        _entries.Clear();
                        _generation++;
                    }

                    _entries[key] = new CacheEntry(snapshot, _clock.GetUtcNow() + Lifetime);
                    return snapshot;
                }
            }

            throw new AuthzUnavailableException();
        }
        finally
        {
            stripe.Release();
        }
    }

    public void Invalidate(Guid tenantId, IReadOnlyCollection<Guid>? userIds = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant is required for cache invalidation.", nameof(tenantId));
        }

        lock (_sync)
        {
            _generation++;
            var entireTenant = userIds is null || userIds.Count == 0 || userIds.Contains(Guid.Empty);
            var affectedUsers = entireTenant ? null : userIds!.ToHashSet();
            foreach (var key in _entries.Keys.Where(key =>
                         key.TenantId == tenantId &&
                         (entireTenant || affectedUsers!.Contains(key.UserId))).ToArray())
            {
                _entries.Remove(key);
            }
        }
    }

    Task IAuthzCacheInvalidator.InvalidateAsync(Guid tenantId, IReadOnlyCollection<Guid>? userIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invalidate(tenantId, userIds);
        return Task.CompletedTask;
    }

    private bool TryRead(CacheKey key, out AuthzSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > _clock.GetUtcNow())
                {
                    snapshot = entry.Snapshot;
                    return true;
                }

                _entries.Remove(key);
            }
        }

        snapshot = null!;
        return false;
    }

    public void Dispose()
    {
        foreach (var stripe in _stripes)
        {
            stripe.Dispose();
        }
    }

    private readonly record struct CacheKey(Guid TenantId, Guid UserId);
    private sealed record CacheEntry(AuthzSnapshot Snapshot, DateTimeOffset ExpiresAt);
}

public static class CachedAuthzResolverRegistration
{
    public static IServiceCollection AddCachedAuthzResolver(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CachedAuthzResolver>();
        services.AddSingleton<IAuthzResolver>(provider => provider.GetRequiredService<CachedAuthzResolver>());
        services.AddSingleton<IAuthzCacheInvalidator>(provider => provider.GetRequiredService<CachedAuthzResolver>());
        services.AddSingleton<AccessAuthzChangedHandler>();
        return services;
    }
}
