using System.Text.Json;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public enum OwnerInvitationResendStatus { Accepted, NotFound }

public sealed class OwnerInvitationResendService(NpgsqlDataSource dataSource,
    InvitationDeliveryCoordinator deliveryCoordinator)
{
    public async Task<OwnerInvitationResendStatus> ResendAsync(Guid tenantId,
        IdentityAdminActor actor, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || actor.Sub == Guid.Empty)
            return OwnerInvitationResendStatus.NotFound;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)",
            connection, transaction))
        {
            guard.Parameters.AddWithValue("key", AccessAuditWriter.AuditLock);
            await guard.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true)",
            connection, transaction))
        {
            scope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var tenant = new NpgsqlCommand(
            "SELECT status FROM tenants WHERE id = @tenant FOR UPDATE", connection, transaction))
        {
            tenant.Parameters.AddWithValue("tenant", tenantId);
            if (await tenant.ExecuteScalarAsync(cancellationToken) as string != "provisioning")
                return OwnerInvitationResendStatus.NotFound;
        }
        Guid ownerRole;
        await using (var role = new NpgsqlCommand("""
            SELECT id FROM roles WHERE tenant_id IS NULL AND is_system AND name = 'owner'
            """, connection, transaction))
            ownerRole = await role.ExecuteScalarAsync(cancellationToken) is Guid id
                ? id : Guid.Empty;
        if (ownerRole == Guid.Empty) return OwnerInvitationResendStatus.NotFound;

        Guid invitationId = Guid.Empty;
        string email = "";
        Guid? keycloakUserId = null;
        await using (var command = new NpgsqlCommand("""
            SELECT id, email, role_ids::text, keycloak_user_id
            FROM invitations WHERE tenant_id = @tenant AND status IN ('pending','expired')
            ORDER BY created_at LIMIT 1 FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var roles = JsonSerializer.Deserialize<Guid[]>(reader.GetString(2)) ?? [];
                if (roles.Length == 1 && roles[0] == ownerRole)
                {
                    invitationId = reader.GetGuid(0);
                    email = reader.GetString(1);
                    keycloakUserId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
                }
            }
        }
        if (invitationId == Guid.Empty) return OwnerInvitationResendStatus.NotFound;
        var code = InvitationToken.Issue(tenantId);
        await using (var update = new NpgsqlCommand("""
            UPDATE invitations SET status = 'pending', token_hash = @hash,
                expires_at = now() + interval '7 days', invited_by = @actor
            WHERE id = @id AND tenant_id = @tenant
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("hash", code.SecretHash);
            update.Parameters.AddWithValue("actor", actor.Sub);
            update.Parameters.AddWithValue("id", invitationId);
            update.Parameters.AddWithValue("tenant", tenantId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actor.Sub,
            "human", "access.owner-invitation.resent", "invitation", invitationId.ToString("D"),
            "owner-invitation-resend", cancellationToken, acr: actor.Acr);
        await transaction.CommitAsync(cancellationToken);
        await deliveryCoordinator.DeliverAsync(invitationId, tenantId, email, code.Value,
            keycloakUserId, actor, cancellationToken);
        return OwnerInvitationResendStatus.Accepted;
    }
}
