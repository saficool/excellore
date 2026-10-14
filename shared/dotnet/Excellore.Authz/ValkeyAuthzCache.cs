using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

namespace Excellore.Authz;

public readonly record struct AuthzCacheGeneration(long Tenant, long User);
public sealed record ValkeyAuthzRead(AuthzSnapshot? Snapshot, AuthzCacheGeneration Generation);

public interface IAuthzDistributedSnapshotCache
{
    Task<ValkeyAuthzRead> ReadAsync(Guid userId, Guid tenantId,
        CancellationToken cancellationToken = default);
    Task<bool> StoreIfCurrentAsync(Guid userId, Guid tenantId, AuthzSnapshot snapshot,
        AuthzCacheGeneration generation, CancellationToken cancellationToken = default);
    Task InvalidateAsync(Guid tenantId, IReadOnlyCollection<Guid>? userIds = null,
        CancellationToken cancellationToken = default);
}

/// <summary>A 60-second Valkey authorization snapshot cache with race-safe invalidation.</summary>
public sealed class ValkeyAuthzCache : IAuthzDistributedSnapshotCache
{
    private const int MaxPayloadBytes = 64 * 1024;
    private const string StoreScript = """
        if (redis.call('GET', KEYS[1]) or '0') ~= ARGV[1] then return 0 end
        if (redis.call('GET', KEYS[2]) or '0') ~= ARGV[2] then return 0 end
        redis.call('SET', KEYS[3], ARGV[3], 'EX', 60)
        return 1
        """;
    private const string InvalidateScript = """
        local generation = redis.call('INCR', KEYS[1])
        redis.call('EXPIRE', KEYS[1], 300)
        return generation
        """;

    private readonly IDatabase _database;
    private readonly string _appPrefix;

    public ValkeyAuthzCache(IConnectionMultiplexer connection, string appPrefix)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateAppPrefix(appPrefix);

        _database = connection.GetDatabase();
        _appPrefix = appPrefix;
    }

    internal static void ValidateAppPrefix(string appPrefix)
    {
        if (string.IsNullOrWhiteSpace(appPrefix) ||
            appPrefix.Any(character => !char.IsAsciiLetterLower(character) &&
                                       !char.IsAsciiDigit(character) && character != '-'))
        {
            throw new ArgumentException("A lowercase app cache prefix is required.", nameof(appPrefix));
        }
    }

    public async Task<ValkeyAuthzRead> ReadAsync(Guid userId, Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, tenantId);
        cancellationToken.ThrowIfCancellationRequested();
        RedisValue[] values;
        try
        {
            values = await _database.StringGetAsync([
                TenantGenerationKey(tenantId), UserGenerationKey(tenantId, userId), SnapshotKey(tenantId, userId)
            ]);
        }
        catch (Exception error) when (error is RedisException or ObjectDisposedException)
        {
            throw new AuthzUnavailableException();
        }
        cancellationToken.ThrowIfCancellationRequested();

        var generation = new AuthzCacheGeneration(ParseGeneration(values[0]), ParseGeneration(values[1]));
        if (values[2].IsNull)
        {
            return new ValkeyAuthzRead(null, generation);
        }

        try
        {
            var raw = (byte[])values[2]!;
            if (raw.Length > MaxPayloadBytes)
            {
                return new ValkeyAuthzRead(null, generation);
            }

            var entry = JsonSerializer.Deserialize<CacheEntry>(raw);
            if (entry is null || entry.Schema != 1 || entry.TenantId != tenantId ||
                entry.TenantGeneration != generation.Tenant ||
                entry.UserGeneration != generation.User ||
                entry.Permissions is null || entry.Permissions.Any(string.IsNullOrWhiteSpace) ||
                !Enum.IsDefined(entry.Decision) ||
                (entry.Decision == AuthzDecision.Allow &&
                 (entry.TenantStatus != "active" || entry.MembershipStatus != "active")))
            {
                return new ValkeyAuthzRead(null, generation);
            }

            var snapshot = new AuthzSnapshot(entry.TenantId, entry.Decision,
                entry.TenantStatus, entry.MembershipStatus,
                ImmutableHashSet.CreateRange(StringComparer.Ordinal, entry.Permissions));
            return new ValkeyAuthzRead(snapshot, generation);
        }
        catch (JsonException)
        {
            return new ValkeyAuthzRead(null, generation);
        }
    }

    public async Task<bool> StoreIfCurrentAsync(Guid userId, Guid tenantId,
        AuthzSnapshot snapshot, AuthzCacheGeneration generation,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, tenantId);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TenantId != tenantId || snapshot.Permissions is null ||
            generation.Tenant < 0 || generation.User < 0 ||
            !Enum.IsDefined(snapshot.Decision) ||
            (snapshot.Decision == AuthzDecision.Allow && !snapshot.IsActiveMember))
        {
            throw new ArgumentException("Invalid authorization snapshot or generation.", nameof(snapshot));
        }

        var entry = new CacheEntry(1, generation.Tenant, generation.User, tenantId,
            snapshot.Decision, snapshot.TenantStatus, snapshot.MembershipStatus,
            snapshot.Permissions.ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (bytes.Length > MaxPayloadBytes)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await _database.ScriptEvaluateAsync(StoreScript,
                [TenantGenerationKey(tenantId), UserGenerationKey(tenantId, userId), SnapshotKey(tenantId, userId)],
                [generation.Tenant.ToString(CultureInfo.InvariantCulture),
                    generation.User.ToString(CultureInfo.InvariantCulture), bytes]);
            cancellationToken.ThrowIfCancellationRequested();
            return (long)result == 1;
        }
        catch (Exception error) when (error is RedisException or ObjectDisposedException)
        {
            throw new AuthzUnavailableException();
        }
    }

    public async Task InvalidateAsync(Guid tenantId, IReadOnlyCollection<Guid>? userIds = null,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant is required for cache invalidation.", nameof(tenantId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (userIds is null || userIds.Count == 0 || userIds.Contains(Guid.Empty))
            {
                await _database.ScriptEvaluateAsync(InvalidateScript, [TenantGenerationKey(tenantId)]);
            }
            else
            {
                foreach (var userId in userIds.Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _database.ScriptEvaluateAsync(InvalidateScript,
                        [UserGenerationKey(tenantId, userId)]);
                }
            }
        }
        catch (Exception error) when (error is RedisException or ObjectDisposedException)
        {
            throw new AuthzUnavailableException();
        }
    }

    private static long ParseGeneration(RedisValue value)
    {
        if (value.IsNull)
        {
            return 0;
        }

        if (!long.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 0)
        {
            throw new AuthzUnavailableException();
        }

        return parsed;
    }

    private static void ValidateIds(Guid userId, Guid tenantId)
    {
        if (userId == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("A user and tenant are required for authorization caching.");
        }
    }

    private RedisKey TenantGenerationKey(Guid tenantId) => $"{_appPrefix}:authz:tenant-generation:{tenantId:D}";
    private RedisKey UserGenerationKey(Guid tenantId, Guid userId) =>
        $"{_appPrefix}:authz:user-generation:{tenantId:D}:{userId:D}";
    private RedisKey SnapshotKey(Guid tenantId, Guid userId) =>
        $"{_appPrefix}:authz:{tenantId:D}:{userId:D}";

    private sealed record CacheEntry(int Schema, long TenantGeneration, long UserGeneration,
        Guid TenantId, AuthzDecision Decision, string? TenantStatus, string? MembershipStatus,
        string[] Permissions);
}
