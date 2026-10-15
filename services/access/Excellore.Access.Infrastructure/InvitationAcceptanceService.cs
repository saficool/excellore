using System.Text.Json;
using Excellore.Auth;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record AcceptInvitationRequest(Guid InvitationId, string? OneTimeCode);

public enum InvitationAcceptanceStatus { Accepted, Invalid, Unavailable, Conflict }

public sealed record InvitationAcceptanceResult(InvitationAcceptanceStatus Status, Guid? TenantId = null);

public sealed class InvitationAcceptanceService(NpgsqlDataSource dataSource, ICurrentUser user,
    AccessResolutionService resolver)
{
    public async Task<InvitationAcceptanceResult> AcceptAsync(AcceptInvitationRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.InvitationId == Guid.Empty ||
            !InvitationToken.TryRead(request.OneTimeCode, out var tenantId, out var suppliedHash))
            return new(InvitationAcceptanceStatus.Invalid);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", ("key", AccessAuditWriter.AuditLock));
        await ExecuteAsync("SELECT set_config('app.tenant_id', @tenant, true)",
            ("tenant", tenantId.ToString("D")));
        await using (var exists = Command("SELECT EXISTS (SELECT 1 FROM tenants WHERE id = @tenant)",
            ("tenant", tenantId)))
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
                return new(InvitationAcceptanceStatus.Unavailable);

        Guid invitationId = Guid.Empty;
        Guid invitedBy = Guid.Empty;
        string invitedEmail = "";
        string roleJson = "[]";
        string groupJson = "[]";
        string status = "";
        DateTimeOffset expiresAt = default;
        byte[] storedHash = [];
        await using (var command = Command("""
            SELECT id, invited_by, email, role_ids::text, group_ids::text, status,
                   expires_at, token_hash
            FROM invitations WHERE id = @invitation AND tenant_id = @tenant
              AND token_hash = @hash FOR UPDATE
            """, ("invitation", request.InvitationId), ("tenant", tenantId),
            ("hash", suppliedHash)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                invitationId = reader.GetGuid(0);
                invitedBy = reader.GetGuid(1);
                invitedEmail = reader.GetString(2);
                roleJson = reader.GetString(3);
                groupJson = reader.GetString(4);
                status = reader.GetString(5);
                expiresAt = reader.GetFieldValue<DateTimeOffset>(6);
                storedHash = reader.GetFieldValue<byte[]>(7);
            }
        }
        if (invitationId == Guid.Empty)
            return await DenyAsync(InvitationAcceptanceStatus.Unavailable, "invitation-unavailable");
        if (!InvitationToken.Matches(storedHash, suppliedHash) || status != "pending" ||
            expiresAt <= DateTimeOffset.UtcNow)
            return await DenyAsync(InvitationAcceptanceStatus.Unavailable, "invitation-unavailable", invitationId);

        string? tenantStatus = null;
        await using (var command = Command("SELECT status FROM tenants WHERE id = @tenant FOR UPDATE",
            ("tenant", tenantId)))
            tenantStatus = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (tenantStatus is not ("active" or "provisioning"))
            return await DenyAsync(InvitationAcceptanceStatus.Unavailable, "tenant-inactive", invitationId);

        string? actualEmail = null;
        bool verified = false;
        string? identityStatus = null;
        await using (var command = Command("""
            SELECT email, email_verified, status FROM identities WHERE user_id = @user
            """, ("user", user.UserId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                actualEmail = reader.GetString(0);
                verified = reader.GetBoolean(1);
                identityStatus = reader.GetString(2);
            }
        }
        if (!verified || identityStatus != "active" ||
            !string.Equals(actualEmail, invitedEmail, StringComparison.OrdinalIgnoreCase) ||
            invitedBy == user.UserId)
            return await DenyAsync(InvitationAcceptanceStatus.Unavailable, "identity-mismatch", invitationId);

        Guid[] roleIds;
        Guid[] groupIds;
        try
        {
            roleIds = JsonSerializer.Deserialize<Guid[]>(roleJson) ?? [];
            groupIds = JsonSerializer.Deserialize<Guid[]>(groupJson) ?? [];
        }
        catch (JsonException)
        {
            return await DenyAsync(InvitationAcceptanceStatus.Conflict, "invitation-invalid", invitationId);
        }
        if (roleIds.Length is 0 or > 25 || roleIds.Distinct().Count() != roleIds.Length ||
            groupIds.Length > 25 || groupIds.Distinct().Count() != groupIds.Length)
            return await DenyAsync(InvitationAcceptanceStatus.Conflict, "invitation-invalid", invitationId);

        var ownerRole = false;
        foreach (var roleId in roleIds)
        {
            await using var command = Command("""
                SELECT name FROM roles WHERE id = @role AND deleted_at IS NULL
                  AND (tenant_id IS NULL OR tenant_id = @tenant)
                """, ("role", roleId), ("tenant", tenantId));
            if (await command.ExecuteScalarAsync(cancellationToken) is not string roleName)
                return await DenyAsync(InvitationAcceptanceStatus.Conflict, "role-unavailable", invitationId);
            if (roleName == "owner") ownerRole = true;
        }
        if (tenantStatus == "provisioning" && (!ownerRole || roleIds.Length != 1 || groupIds.Length != 0))
            return await DenyAsync(InvitationAcceptanceStatus.Conflict, "owner-invitation-required", invitationId);
        if (tenantStatus == "active" && ownerRole)
            return await DenyAsync(InvitationAcceptanceStatus.Conflict, "ownership-transfer-required", invitationId);
        HashSet<string>? inviterPermissions = null;
        if (tenantStatus == "active")
        {
            var inviter = await resolver.ResolveAsync(invitedBy, tenantId, cancellationToken);
            if (!inviter.Allowed || !inviter.Permissions.Contains("access.member.invite"))
                return await DenyAsync(InvitationAcceptanceStatus.Conflict, "inviter-unavailable", invitationId);
            inviterPermissions = inviter.Permissions.ToHashSet(StringComparer.Ordinal);
            foreach (var roleId in roleIds)
            {
                var role = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
                    tenantId, roleId, cancellationToken);
                if (role is null || AccessRoleGrantPolicy.Check(role, inviterPermissions, true) is not null)
                    return await DenyAsync(InvitationAcceptanceStatus.Conflict,
                        "role-policy-changed", invitationId);
            }
        }
        foreach (var groupId in groupIds)
        {
            await using var command = Command("SELECT EXISTS (SELECT 1 FROM groups WHERE id = @group AND tenant_id = @tenant)",
                ("group", groupId), ("tenant", tenantId));
            if (await command.ExecuteScalarAsync(cancellationToken) is not true)
                return await DenyAsync(InvitationAcceptanceStatus.Conflict, "group-unavailable", invitationId);
            await using var roleCommand = Command("""
                SELECT role_id FROM group_role_assignments
                WHERE tenant_id = @tenant AND group_id = @group AND revoked_at IS NULL
                  AND (expires_at IS NULL OR expires_at > now())
                """, ("tenant", tenantId), ("group", groupId));
            var groupRoleIds = new List<Guid>();
            await using (var reader = await roleCommand.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) groupRoleIds.Add(reader.GetGuid(0));
            foreach (var roleId in groupRoleIds)
            {
                var role = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
                    tenantId, roleId, cancellationToken);
                if (role is null || inviterPermissions is null ||
                    AccessRoleGrantPolicy.Check(role, inviterPermissions, true) is not null)
                    return await DenyAsync(InvitationAcceptanceStatus.Conflict,
                        "group-role-policy-changed", invitationId);
            }
        }

        string? memberStatus = null;
        await using (var command = Command("""
            SELECT status FROM tenant_members WHERE tenant_id = @tenant AND user_id = @user FOR UPDATE
            """, ("tenant", tenantId), ("user", user.UserId)))
            memberStatus = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (memberStatus is "active" or "suspended")
            return await DenyAsync(InvitationAcceptanceStatus.Conflict, "membership-exists", invitationId);
        if (memberStatus == "removed")
        {
            await ExecuteAsync("""
                UPDATE role_assignments SET revoked_at = now()
                WHERE tenant_id = @tenant AND user_id = @user AND revoked_at IS NULL
                """, ("tenant", tenantId), ("user", user.UserId));
            await ExecuteAsync("""
                DELETE FROM group_members WHERE tenant_id = @tenant AND user_id = @user
                """, ("tenant", tenantId), ("user", user.UserId));
        }

        Guid membershipId;
        await using (var command = Command("""
            INSERT INTO tenant_members (tenant_id, user_id, status, invited_by, joined_at)
            VALUES (@tenant, @user, 'active', @inviter, now())
            ON CONFLICT (tenant_id, user_id) DO UPDATE SET status = 'active',
                invited_by = EXCLUDED.invited_by, joined_at = now(), updated_at = now(),
                membership_id = gen_random_uuid()
            RETURNING membership_id
            """, ("tenant", tenantId), ("user", user.UserId), ("inviter", invitedBy)))
            membershipId = (Guid)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Membership insert failed."));
        foreach (var roleId in roleIds)
        {
            await ExecuteAsync("""
                INSERT INTO role_assignments (id, tenant_id, user_id, role_id, reason, granted_by)
                VALUES (@id, @tenant, @user, @role, 'invitation-accepted', @inviter)
                ON CONFLICT (tenant_id, user_id, role_id) WHERE revoked_at IS NULL DO NOTHING
                """, ("id", Guid.NewGuid()), ("tenant", tenantId), ("user", user.UserId),
                ("role", roleId), ("inviter", invitedBy));
        }
        foreach (var groupId in groupIds)
            await ExecuteAsync("""
                INSERT INTO group_members (tenant_id, group_id, user_id)
                VALUES (@tenant, @group, @user) ON CONFLICT DO NOTHING
                """, ("tenant", tenantId), ("group", groupId), ("user", user.UserId));
        await ExecuteAsync("""
            UPDATE invitations SET status = 'accepted', accepted_by = @user, accepted_at = now()
            WHERE id = @id AND tenant_id = @tenant AND status = 'pending'
            """, ("user", user.UserId), ("id", invitationId), ("tenant", tenantId));
        if (tenantStatus == "provisioning")
            await ExecuteAsync("UPDATE tenants SET status = 'active', updated_at = now() WHERE id = @tenant",
                ("tenant", tenantId));
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, user.UserId,
            "human", "access.invitation.accepted", "invitation", invitationId.ToString("D"),
            "invitation-accepted", cancellationToken,
            afterJson: JsonSerializer.Serialize(new { membershipId, userId = user.UserId }), acr: user.Acr);
        await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction, tenantId,
            user.UserId, cancellationToken);
        await WriteMemberEventAsync();
        await transaction.CommitAsync(cancellationToken);
        return new(InvitationAcceptanceStatus.Accepted, tenantId);

        async Task<InvitationAcceptanceResult> DenyAsync(InvitationAcceptanceStatus denied,
            string reason, Guid? id = null)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, user.UserId,
                "human", "access.invitation.accept.denied", "invitation",
                id?.ToString("D") ?? "unknown", reason, cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(denied);
        }

        async Task WriteMemberEventAsync()
        {
            var id = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(new
            {
                specversion = "1.0", id, type = "tenant.member.added.v1", source = "excellore/access",
                time = DateTimeOffset.UtcNow, tenantid = tenantId, correlationid = Guid.NewGuid(),
                actor = new { sub = user.UserId.ToString("D"), type = "human" },
                data = new { tenantId, userId = user.UserId, membershipId }
            });
            await ExecuteAsync("""
                INSERT INTO outbox (id, type, tenant_id, payload)
                VALUES (@id, 'tenant.member.added.v1', @tenant, @payload::jsonb)
                """, ("id", id), ("tenant", tenantId), ("payload", payload));
        }

        async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = Command(sql, parameters);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        NpgsqlCommand Command(string sql, params (string Name, object Value)[] parameters)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return command;
        }
    }
}
