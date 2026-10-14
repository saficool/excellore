using System.Diagnostics;

namespace Excellore.Platform;

/// <summary>Shared activity source for tenant-scoped platform operations.</summary>
public static class ExcelloreTelemetry
{
    public const string ActivitySourceName = "Excellore.Platform";
    internal static readonly ActivitySource Source = new(ActivitySourceName);

    internal static Activity? StartTenantActivity(string operation, Guid tenantId)
    {
        var activity = Source.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag("tenant.id", tenantId.ToString("D"));
        return activity;
    }
}
