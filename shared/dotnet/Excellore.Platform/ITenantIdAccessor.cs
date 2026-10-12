namespace Excellore.Platform;

/// <summary>The tenant selected by a validated request gate or worker scope.</summary>
public interface ITenantIdAccessor
{
    Guid TenantId { get; }
}
