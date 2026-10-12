using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Excellore.Authz;

/// <summary>Invalidates the shared snapshot, then clears in-memory caches on every subscribed replica.</summary>
public sealed class ValkeyAuthzInvalidationFanout : IAuthzCacheInvalidator, IHostedService
{
    private readonly CachedAuthzResolver _local;
    private readonly IAuthzDistributedSnapshotCache _distributed;
    private readonly ISubscriber _subscriber;
    private readonly RedisChannel _channel;
    private volatile bool _started;

    public ValkeyAuthzInvalidationFanout(CachedAuthzResolver local,
        IAuthzDistributedSnapshotCache distributed, IConnectionMultiplexer connection,
        string appPrefix)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(distributed);
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(appPrefix) ||
            appPrefix.Any(character => !char.IsAsciiLetterLower(character) &&
                                       !char.IsAsciiDigit(character) && character != '-'))
        {
            throw new ArgumentException("A lowercase app cache prefix is required.", nameof(appPrefix));
        }

        _local = local;
        _distributed = distributed;
        _subscriber = connection.GetSubscriber();
        _channel = RedisChannel.Literal($"{appPrefix}:authz:invalidate");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _subscriber.SubscribeAsync(_channel, OnMessage);
            _started = true;
        }
        catch (Exception error) when (error is RedisException or ObjectDisposedException)
        {
            throw new AuthzUnavailableException();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _subscriber.UnsubscribeAsync(_channel, OnMessage);
            _started = false;
        }
        catch (Exception error) when (error is RedisException or ObjectDisposedException)
        {
            throw new AuthzUnavailableException();
        }
    }

    public async Task InvalidateAsync(Guid tenantId, IReadOnlyCollection<Guid>? userIds = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started)
        {
            throw new AuthzUnavailableException();
        }

        _local.Invalidate(tenantId, userIds);
        await _distributed.InvalidateAsync(tenantId, userIds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Only the tenant ID is broadcast; user IDs and authorization details stay off this channel.
            if (await _subscriber.PublishAsync(_channel, tenantId.ToString("D")) == 0)
            {
                throw new AuthzUnavailableException();
            }
        }
        catch (Exception error) when (error is RedisException or ObjectDisposedException)
        {
            throw new AuthzUnavailableException();
        }
    }

    private void OnMessage(RedisChannel channel, RedisValue value)
    {
        if (Guid.TryParse(value.ToString(), out var tenantId) && tenantId != Guid.Empty)
        {
            _local.Invalidate(tenantId);
        }
    }
}
