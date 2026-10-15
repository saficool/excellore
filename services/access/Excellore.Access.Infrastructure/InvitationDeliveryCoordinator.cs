using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed class InvitationDeliveryCoordinator(NpgsqlDataSource dataSource,
    IIdentityAdmin identityAdmin, IInvitationDelivery delivery)
{
    public async Task DeliverAsync(Guid invitationId, Guid tenantId, string email,
        string oneTimeCode, Guid? keycloakUserId, IdentityAdminActor actor,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (keycloakUserId is null)
            {
                var identity = await identityAdmin.EnsureInvitationIdentityAsync(email,
                    invitationId, actor, cancellationToken);
                if (identity.Status == IdentityCreationStatus.Unavailable)
                {
                    await AuditDeliveryFailureAsync(tenantId, invitationId, actor,
                        "identity-unavailable", cancellationToken);
                    return;
                }
                keycloakUserId = identity.UserId;
                if (keycloakUserId is Guid createdId)
                    await StoreKeycloakUserAsync(tenantId, invitationId, createdId,
                        actor, cancellationToken);
                else
                    await AuditDeliveryAsync(tenantId, invitationId, actor,
                        "access.identity.invitation-checked", "identity-existing", cancellationToken);
            }
            if (keycloakUserId is Guid userId &&
                !await identityAdmin.SendRequiredActionsAsync(userId, invitationId, actor,
                    cancellationToken))
            {
                await AuditDeliveryFailureAsync(tenantId, invitationId, actor,
                    "actions-email-unavailable", cancellationToken);
                return;
            }
            if (keycloakUserId is not null)
                await AuditDeliveryAsync(tenantId, invitationId, actor,
                    "access.identity.actions-emailed", "required-actions-sent", cancellationToken);
            await delivery.SendAsync(email, invitationId, oneTimeCode, cancellationToken);
            await AuditDeliveryAsync(tenantId, invitationId, actor,
                "access.invitation.delivered", "invitation-email-sent", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // SMTP and HTTP exception messages can contain email addresses or tokens.
            await AuditDeliveryFailureAsync(tenantId, invitationId, actor,
                "delivery-unavailable", cancellationToken);
        }
    }

    private async Task StoreKeycloakUserAsync(Guid tenantId, Guid invitationId,
        Guid keycloakUserId, IdentityAdminActor actor, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)",
            connection, transaction))
        {
            guard.Parameters.AddWithValue("key", AccessAuditWriter.AuditLock);
            await guard.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true)",
            connection, transaction);
        scope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
        await scope.ExecuteNonQueryAsync(cancellationToken);
        await using var update = new NpgsqlCommand("""
            UPDATE invitations SET keycloak_user_id = @user
            WHERE id = @id AND tenant_id = @tenant AND status = 'pending'
            """, connection, transaction);
        update.Parameters.AddWithValue("user", keycloakUserId);
        update.Parameters.AddWithValue("id", invitationId);
        update.Parameters.AddWithValue("tenant", tenantId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actor.Sub,
            "human", "access.identity.invited", "identity", keycloakUserId.ToString("D"),
            "invitation-created-identity", cancellationToken, acr: actor.Acr);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task AuditDeliveryFailureAsync(Guid tenantId, Guid invitationId,
        IdentityAdminActor actor, string reason, CancellationToken cancellationToken)
        => await AuditDeliveryAsync(tenantId, invitationId, actor,
            "access.invitation.delivery.failed", reason, cancellationToken);

    private async Task AuditDeliveryAsync(Guid tenantId, Guid invitationId,
        IdentityAdminActor actor, string action, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true)",
            connection, transaction);
        scope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
        await scope.ExecuteNonQueryAsync(cancellationToken);
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actor.Sub,
            "human", action, "invitation",
            invitationId.ToString("D"), reason, cancellationToken, acr: actor.Acr);
        await transaction.CommitAsync(cancellationToken);
    }
}
