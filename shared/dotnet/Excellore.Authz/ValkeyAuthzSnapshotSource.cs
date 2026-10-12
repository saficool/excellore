namespace Excellore.Authz;

/// <summary>Reads the distributed cache before asking Access for a fresh decision.</summary>
public sealed class ValkeyAuthzSnapshotSource(
    IAuthzDistributedSnapshotCache cache,
    IAuthzSnapshotSource access) : IAuthzSnapshotSource
{
    public async Task<AuthzSnapshot> ResolveFreshAsync(Guid userId, Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("A user and tenant are required to resolve authorization.");
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            ValkeyAuthzRead read;
            try
            {
                read = await cache.ReadAsync(userId, tenantId, cancellationToken);
            }
            catch (AuthzUnavailableException)
            {
                // Access remains the authority when Valkey cannot be reached.
                return await ResolveFromAccessAsync(userId, tenantId, cancellationToken);
            }

            if (read.Snapshot is { } cached)
            {
                return Validate(cached, tenantId);
            }

            var fresh = await ResolveFromAccessAsync(userId, tenantId, cancellationToken);
            try
            {
                if (await cache.StoreIfCurrentAsync(userId, tenantId, fresh,
                        read.Generation, cancellationToken))
                {
                    return fresh;
                }
            }
            catch (AuthzUnavailableException)
            {
                // The decision came from Access during this request, so it can be returned uncached.
                return fresh;
            }
        }

        throw new AuthzUnavailableException();
    }

    private async Task<AuthzSnapshot> ResolveFromAccessAsync(Guid userId, Guid tenantId,
        CancellationToken cancellationToken) =>
        Validate(await access.ResolveFreshAsync(userId, tenantId, cancellationToken), tenantId);

    private static AuthzSnapshot Validate(AuthzSnapshot? snapshot, Guid tenantId)
    {
        if (snapshot is null || snapshot.TenantId != tenantId || snapshot.Permissions is null ||
            !Enum.IsDefined(snapshot.Decision) ||
            (snapshot.Decision == AuthzDecision.Allow && !snapshot.IsActiveMember))
        {
            throw new AuthzUnavailableException();
        }

        return snapshot;
    }
}
