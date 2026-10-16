using Excellore.Access.Infrastructure;

namespace Excellore.Access.Worker;

public sealed class AccessTenantMaintenanceJob(AccessExpiryService expiry, AuditExportBuilder exports)
{
    public async Task RunAsync(Guid machineActorId, CancellationToken cancellationToken)
    {
        await expiry.ExpireAsync(machineActorId, cancellationToken);
        for (var processed = 0; processed < 4; processed++)
            if (!await exports.ProcessOneAsync(machineActorId, cancellationToken)) break;
    }
}
