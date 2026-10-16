using Excellore.Contracts;
using Excellore.Platform;

namespace Excellore.Profile.Worker;

public sealed class ProfileMembershipSubscriber(IEventBus eventBus,
    ProfileMembershipProjector projector, ProfileIdentityMirrorProjector identity,
    ProfileTenantErasureProjector erasure,
    ProfilePersonalExportProjector exports,
    ProfileTenantExportProjector tenantExports,
    ProfileUserErasureProjector userErasure,
    ILogger<ProfileMembershipSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var types = new[]
        {
            TenantMemberAddedV1.EventType, TenantMemberSuspendedV1.EventType,
            TenantMemberReactivatedV1.EventType, TenantMemberRemovedV1.EventType
        };
        var handlers = types.ToDictionary(type => type,
            type => (PlatformEventHandler)((body, token) => projector.ApplyAsync(type, body, token)));
        handlers.Add(IdentityUserUpdatedV1.EventType,
            (body, token) => identity.ApplyAsync(body, token));
        handlers.Add(TenantDeletedV1.EventType,
            (body, token) => erasure.ApplyAsync(body, token));
        handlers.Add(ProfileExportRequestedV1.EventType,
            (body, token) => exports.BuildProfileAsync(body, token));
        handlers.Add(AccessUserExportReadyV1.EventType,
            (body, token) => exports.CompleteAsync(body, token));
        handlers.Add(TenantExportRequestedV1.EventType,
            (body, token) => tenantExports.ApplyAsync(body, token));
        handlers.Add(AccessErasureConfirmedV1.EventType,
            (body, token) => userErasure.ApplyAsync(body, token));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var subscription = await eventBus.SubscribeAsync("profile", handlers,
                    stoppingToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                logger.LogWarning("Profile event subscription unavailable; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
