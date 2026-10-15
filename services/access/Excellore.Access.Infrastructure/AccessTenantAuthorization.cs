using System.Collections.Immutable;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

/// <summary>Access resolves its own tenant requests from its authority database.</summary>
public sealed class AccessAuthorityResolver(AccessResolutionService service) : IAuthzResolver
{
    public async Task<AuthzSnapshot> ResolveAsync(Guid userId, Guid tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.ResolveAsync(userId, tenantId, cancellationToken);
            return new AuthzSnapshot(tenantId, result.Allowed ? AuthzDecision.Allow : AuthzDecision.Deny,
                result.Allowed ? "active" : null, result.Allowed ? "active" : null,
                result.Permissions.ToImmutableHashSet(StringComparer.Ordinal));
        }
        catch (NpgsqlException)
        {
            throw new AuthzUnavailableException();
        }
    }
}

/// <summary>Read privilege flags from the deployed code-first catalog; unknown keys require MFA.</summary>
public sealed class AccessPrivilegeClassifier(NpgsqlDataSource dataSource) : IPrivilegeClassifier
{
    public bool IsPrivileged(string permission) => true;

    public async ValueTask<bool> IsPrivilegedAsync(string permission,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT privileged FROM permissions WHERE key = @key", connection);
            command.Parameters.AddWithValue("key", permission);
            return await command.ExecuteScalarAsync(cancellationToken) is not false;
        }
        catch (NpgsqlException)
        {
            throw new AuthzUnavailableException();
        }
    }
}
