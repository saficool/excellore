using Npgsql;

namespace Excellore.Access.Infrastructure;

/// <summary>Keeps the tenant-export worker on the shared Access audit chain.</summary>
public static class TenantExportCompletionAudit
{
    public static Task WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, Guid machineActorId, Guid exportId,
        CancellationToken cancellationToken = default) =>
        AccessAuditWriter.WriteAsync(connection, transaction, tenantId, machineActorId,
            "service", "access.tenant.export-ready", "tenant-export", exportId.ToString("D"),
            "worker-complete", cancellationToken);
}
