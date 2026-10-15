using System.Text.Json;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record ListedInvitation(Guid Id, string Email, Guid[] RoleIds, Guid[] GroupIds,
    string Status, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);
public sealed record MyPendingInvitation(Guid Id, Guid TenantId, string TenantName,
    DateTimeOffset ExpiresAt);

public enum InvitationRevocationStatus { Revoked, NotFound, Forbidden }

public sealed class InvitationManagementService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext, ICurrentUser user, AccessResolutionService resolver)
{
    public async Task<IReadOnlyList<MyPendingInvitation>> ListMineAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        string? email = null;
        await using (var identity = new NpgsqlCommand("""
            SELECT email FROM identities WHERE user_id = @user AND email_verified AND status = 'active'
            """, connection))
        {
            identity.Parameters.AddWithValue("user", user.UserId);
            email = await identity.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (email is null) return [];
        var tenants = new List<(Guid Id, string Name)>();
        await using (var tenantCommand = new NpgsqlCommand("""
            SELECT id, name FROM tenants WHERE status IN ('active','provisioning') ORDER BY id
            """, connection))
        await using (var reader = await tenantCommand.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                tenants.Add((reader.GetGuid(0), reader.GetString(1)));

        var pending = new List<MyPendingInvitation>();
        foreach (var tenant in tenants)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var context = new NpgsqlCommand(
                "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
            {
                context.Parameters.AddWithValue("tenant", tenant.Id.ToString("D"));
                await context.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var command = new NpgsqlCommand("""
                SELECT id, expires_at FROM invitations
                WHERE tenant_id = @tenant AND lower(email) = lower(@email)
                  AND status = 'pending' AND expires_at > now()
                ORDER BY created_at DESC LIMIT 100
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant", tenant.Id);
            command.Parameters.AddWithValue("email", email);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                    pending.Add(new MyPendingInvitation(reader.GetGuid(0), tenant.Id,
                        tenant.Name, reader.GetFieldValue<DateTimeOffset>(1)));
            await transaction.CommitAsync(cancellationToken);
        }
        return pending;
    }

    public async Task<IReadOnlyList<ListedInvitation>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var context = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            context.Parameters.AddWithValue("tenant", tenantContext.TenantId.ToString("D"));
            await context.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand("""
            SELECT id, email, role_ids::text, group_ids::text, status, expires_at, created_at
            FROM invitations WHERE tenant_id = @tenant ORDER BY created_at DESC, id DESC
            LIMIT 200
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId);
        var rows = new List<ListedInvitation>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new ListedInvitation(reader.GetGuid(0), reader.GetString(1),
                    JsonSerializer.Deserialize<Guid[]>(reader.GetString(2)) ?? [],
                    JsonSerializer.Deserialize<Guid[]>(reader.GetString(3)) ?? [],
                    reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetFieldValue<DateTimeOffset>(6)));
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<InvitationRevocationStatus> RevokeAsync(Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)",
            connection, transaction))
        {
            guard.Parameters.AddWithValue("key", AccessAuditWriter.AuditLock);
            await guard.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var context = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            context.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await context.ExecuteNonQueryAsync(cancellationToken);
        }
        var actor = await resolver.ResolveAsync(user.UserId, tenantId, cancellationToken);
        if (!actor.Allowed || !actor.Permissions.Contains("access.member.invite"))
            return await DenyAsync(InvitationRevocationStatus.Forbidden, "permission-missing");
        if (invitationId == Guid.Empty)
            return await DenyAsync(InvitationRevocationStatus.NotFound, "invitation-unavailable");
        await using (var update = new NpgsqlCommand("""
            UPDATE invitations SET status = 'revoked'
            WHERE id = @id AND tenant_id = @tenant AND status = 'pending'
            RETURNING id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", invitationId);
            update.Parameters.AddWithValue("tenant", tenantId);
            if (await update.ExecuteScalarAsync(cancellationToken) is not Guid)
                return await DenyAsync(InvitationRevocationStatus.NotFound, "invitation-unavailable");
        }
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, user.UserId,
            "human", "access.invitation.revoked", "invitation", invitationId.ToString("D"),
            "invitation-revoked", cancellationToken, acr: user.Acr);
        await transaction.CommitAsync(cancellationToken);
        return InvitationRevocationStatus.Revoked;

        async Task<InvitationRevocationStatus> DenyAsync(InvitationRevocationStatus result, string reason)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, user.UserId,
                "human", "access.invitation.revoke.denied", "invitation",
                invitationId == Guid.Empty ? "unknown" : invitationId.ToString("D"),
                reason, cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
    }
}
