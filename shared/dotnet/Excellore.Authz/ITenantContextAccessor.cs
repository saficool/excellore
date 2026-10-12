using Excellore.Platform;

namespace Excellore.Authz;

public interface ITenantContext
{
    Guid TenantId { get; }
    AuthzSnapshot Snapshot { get; }
}

public interface ITenantContextAccessor : ITenantIdAccessor
{
    ITenantContext? Current { get; }
    void Set(Guid tenantId, AuthzSnapshot snapshot);
    void Clear();
}

public sealed class TenantContextAccessor : ITenantContextAccessor
{
    public ITenantContext? Current { get; private set; }
    public Guid TenantId => Current?.TenantId ??
        throw new InvalidOperationException("Tenant context is required for database work.");

    public void Set(Guid tenantId, AuthzSnapshot snapshot)
    {
        if (Current is not null || tenantId == Guid.Empty || snapshot.TenantId != tenantId ||
            !snapshot.IsActiveMember)
        {
            throw new InvalidOperationException("Cannot open tenant context for this authorization decision.");
        }

        Current = new TenantContext(tenantId, snapshot);
    }

    public void Clear() => Current = null;

    private sealed record TenantContext(Guid TenantId, AuthzSnapshot Snapshot) : ITenantContext;
}
