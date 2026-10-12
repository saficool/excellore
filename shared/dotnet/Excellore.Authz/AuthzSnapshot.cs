using System.Collections.Immutable;

namespace Excellore.Authz;

public enum AuthzDecision
{
    Deny,
    Allow
}

public sealed record AuthzSnapshot(
    Guid TenantId,
    AuthzDecision Decision,
    string? TenantStatus,
    string? MembershipStatus,
    ImmutableHashSet<string> Permissions)
{
    public bool IsActiveMember =>
        Decision == AuthzDecision.Allow &&
        string.Equals(TenantStatus, "active", StringComparison.Ordinal) &&
        string.Equals(MembershipStatus, "active", StringComparison.Ordinal);
}
