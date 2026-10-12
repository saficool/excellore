namespace Excellore.Authz;

public interface IAuthzResolver
{
    Task<AuthzSnapshot> ResolveAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);
}

public sealed class AuthzUnavailableException : Exception
{
    public AuthzUnavailableException() : base("Authorization authority is unavailable.") { }
}
