using Excellore.Contracts;
using Excellore.Platform;
using Excellore.Access.Infrastructure;

namespace Excellore.Access.Worker;

public sealed class AccessLifecycleSubscriber(IEventBus eventBus,
    ProfileErasureConfirmationProjector confirmations,
    PersonalExportContributor exports,
    TenantExportCoordinator tenantExports,
    AccessUserErasureProjector userErasure,
    ILogger<AccessLifecycleSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var handlers = new Dictionary<string, PlatformEventHandler>
        {
            [ProfileErasureConfirmedV1.EventType] =
                (body, token) => confirmations.ApplyAsync(body, token),
            [ProfileExportRequestedV1.EventType] =
                (body, token) => exports.ApplyAsync(body, token),
            [TenantExportRequestedV1.EventType] =
                (body, token) => tenantExports.BuildAccessAsync(body, token),
            [ProfileTenantExportReadyV1.EventType] =
                (body, token) => tenantExports.CompleteAsync(body, token),
            [ProfileDeletionRequestedV1.EventType] =
                (body, token) => userErasure.ApplyAsync(body, token)
        };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var subscription = await eventBus.SubscribeAsync(
                    "access", handlers, stoppingToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                logger.LogWarning("Access lifecycle subscription unavailable; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
