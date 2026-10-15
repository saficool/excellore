using System.Net.Mail;
using System.Text.Json;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record CreateInvitationRequest(string? Email, Guid[]? RoleIds,
    Guid[]? GroupIds, string? Reason);
public enum InvitationIssuanceStatus
{
    Accepted, Invalid, Forbidden, StepUpRequired, NotFound, Conflict
}
public sealed record InvitationIssuanceResult(InvitationIssuanceStatus Status);

public sealed class InvitationIssuanceService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext, ICurrentUser user, AccessResolutionService resolver,
    InvitationDeliveryCoordinator deliveryCoordinator)
{
    public Task<InvitationIssuanceResult> CreateAsync(CreateInvitationRequest? request,
        CancellationToken cancellationToken = default) =>
        MutateAsync(null, request, cancellationToken);

    public Task<InvitationIssuanceResult> ResendAsync(Guid invitationId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(invitationId, null, cancellationToken);

    private async Task<InvitationIssuanceResult> MutateAsync(Guid? invitationId,
        CreateInvitationRequest? request, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId;
        var actorId = user.UserId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", ("key", AccessAuditWriter.AuditLock));
        await ExecuteAsync("SELECT set_config('app.tenant_id', @tenant, true)",
            ("tenant", tenantId.ToString("D")));
        var tenantStatus = await ScalarAsync("SELECT status FROM tenants WHERE id = @tenant FOR UPDATE",
            ("tenant", tenantId)) as string;
        if (tenantStatus != "active")
            return await DenyAsync(InvitationIssuanceStatus.NotFound, "tenant-inactive");
        var current = await resolver.ResolveAsync(actorId, tenantId, cancellationToken);
        if (!current.Allowed)
            return await DenyAsync(InvitationIssuanceStatus.NotFound, "membership-inactive");
        var actorPermissions = current.Permissions.ToHashSet(StringComparer.Ordinal);
        if (!actorPermissions.Contains("access.member.invite"))
            return await DenyAsync(InvitationIssuanceStatus.Forbidden, "permission-missing");

        Guid targetId;
        string email;
        Guid[] roleIds;
        Guid[] groupIds;
        Guid? keycloakUserId = null;
        if (invitationId is Guid existingId)
        {
            if (existingId == Guid.Empty)
                return await DenyAsync(InvitationIssuanceStatus.NotFound, "invitation-unavailable");
            string? existingEmail = null;
            string? roleJson = null;
            string? groupJson = null;
            string? existingStatus = null;
            await using (var command = Command("""
                SELECT email, role_ids::text, group_ids::text, status, keycloak_user_id
                FROM invitations WHERE id = @id AND tenant_id = @tenant FOR UPDATE
                """, ("id", existingId), ("tenant", tenantId)))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    existingEmail = reader.GetString(0);
                    roleJson = reader.GetString(1);
                    groupJson = reader.GetString(2);
                    existingStatus = reader.GetString(3);
                    keycloakUserId = reader.IsDBNull(4) ? null : reader.GetGuid(4);
                }
            }
            if (existingEmail is null || existingStatus is not ("pending" or "expired"))
                return await DenyAsync(InvitationIssuanceStatus.NotFound, "invitation-unavailable");
            email = existingEmail;
            try
            {
                roleIds = JsonSerializer.Deserialize<Guid[]>(roleJson!) ?? [];
                groupIds = JsonSerializer.Deserialize<Guid[]>(groupJson!) ?? [];
            }
            catch (JsonException)
            {
                return await DenyAsync(InvitationIssuanceStatus.Conflict, "invitation-invalid");
            }
            targetId = existingId;
        }
        else
        {
            if (!ValidRequest(request))
                return await DenyAsync(InvitationIssuanceStatus.Invalid, "invalid-invitation");
            email = request!.Email!.Trim().ToLowerInvariant();
            roleIds = request.RoleIds!;
            groupIds = request.GroupIds ?? [];
            targetId = Guid.CreateVersion7();
        }
        if (roleIds.Length is 0 or > 25 || roleIds.Any(id => id == Guid.Empty) ||
            roleIds.Distinct().Count() != roleIds.Length || groupIds.Length > 25 ||
            groupIds.Any(id => id == Guid.Empty) || groupIds.Distinct().Count() != groupIds.Length)
            return await DenyAsync(InvitationIssuanceStatus.Conflict, "invitation-invalid");

        var actorEmail = await ScalarAsync("SELECT email FROM identities WHERE user_id = @actor",
            ("actor", actorId)) as string;
        if (string.Equals(actorEmail, email, StringComparison.OrdinalIgnoreCase))
            return await DenyAsync(InvitationIssuanceStatus.Forbidden, "self-invitation");

        foreach (var roleId in roleIds)
        {
            var role = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
                tenantId, roleId, cancellationToken);
            if (role is null)
                return await DenyAsync(InvitationIssuanceStatus.NotFound, "role-unavailable");
            if (AccessRoleGrantPolicy.Check(role, actorPermissions, user.HasRecentMfa) is { } decision)
                return await DenyAsync(MapDecision(decision), "role-policy-denied");
        }
        foreach (var groupId in groupIds)
        {
            if (await ScalarAsync("SELECT EXISTS (SELECT 1 FROM groups WHERE id = @group AND tenant_id = @tenant)",
                    ("group", groupId), ("tenant", tenantId)) is not true)
                return await DenyAsync(InvitationIssuanceStatus.NotFound, "group-unavailable");
            var groupRoleIds = new List<Guid>();
            await using (var command = Command("""
                SELECT role_id FROM group_role_assignments
                WHERE tenant_id = @tenant AND group_id = @group AND revoked_at IS NULL
                  AND (expires_at IS NULL OR expires_at > now())
                """, ("tenant", tenantId), ("group", groupId)))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) groupRoleIds.Add(reader.GetGuid(0));
            foreach (var roleId in groupRoleIds)
            {
                var role = await AccessRoleGrantPolicy.ReadAsync(connection, transaction,
                    tenantId, roleId, cancellationToken);
                if (role is null)
                    return await DenyAsync(InvitationIssuanceStatus.NotFound, "group-role-unavailable");
                if (AccessRoleGrantPolicy.Check(role, actorPermissions, user.HasRecentMfa)
                    is { } decision)
                    return await DenyAsync(MapDecision(decision), "group-role-policy-denied");
            }
        }

        // The response never indicates whether the email belongs to an existing member.
        if (await ScalarAsync("""
            SELECT EXISTS (SELECT 1 FROM tenant_members m JOIN identities i ON i.user_id = m.user_id
              WHERE m.tenant_id = @tenant AND lower(i.email) = lower(@email)
                AND m.status IN ('active','suspended'))
            """, ("tenant", tenantId), ("email", email)) is true)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", "access.invitation.create.denied", "invitation", "existing-member",
                "membership-exists", cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(InvitationIssuanceStatus.Accepted);
        }

        var token = InvitationToken.Issue(tenantId);
        if (invitationId is null)
        {
            await ExecuteAsync("""
                UPDATE invitations SET status = 'expired'
                WHERE tenant_id = @tenant AND lower(email) = lower(@email)
                  AND status = 'pending' AND expires_at <= now()
                """, ("tenant", tenantId), ("email", email));
            if (await ScalarAsync("""
                SELECT EXISTS (SELECT 1 FROM invitations WHERE tenant_id = @tenant
                  AND lower(email) = lower(@email) AND status = 'pending')
                """, ("tenant", tenantId), ("email", email)) is true)
            {
                await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                    "human", "access.invitation.create.duplicate", "invitation", "pending",
                    "invitation-already-pending", cancellationToken, acr: user.Acr);
                await transaction.CommitAsync(cancellationToken);
                return new(InvitationIssuanceStatus.Accepted);
            }
            await ExecuteAsync("""
                INSERT INTO invitations
                  (id, tenant_id, email, role_ids, group_ids, token_hash, status, invited_by, expires_at)
                VALUES (@id, @tenant, @email, @roles::jsonb, @groups::jsonb,
                        @hash, 'pending', @actor, now() + interval '7 days')
                """, ("id", targetId), ("tenant", tenantId), ("email", email),
                ("roles", JsonSerializer.Serialize(roleIds)),
                ("groups", JsonSerializer.Serialize(groupIds)),
                ("hash", token.SecretHash), ("actor", actorId));
        }
        else
        {
            if (await ScalarAsync("""
                SELECT EXISTS (SELECT 1 FROM invitations
                  WHERE tenant_id = @tenant AND lower(email) = lower(@email)
                    AND status = 'pending' AND id <> @id)
                """, ("tenant", tenantId), ("email", email), ("id", targetId)) is true)
                return await DenyAsync(InvitationIssuanceStatus.Conflict, "invitation-already-pending");
            await ExecuteAsync("""
                UPDATE invitations SET status = 'pending', token_hash = @hash,
                    expires_at = now() + interval '7 days', invited_by = @actor,
                    accepted_by = NULL, accepted_at = NULL
                WHERE id = @id AND tenant_id = @tenant
                """, ("hash", token.SecretHash), ("actor", actorId),
                ("id", targetId), ("tenant", tenantId));
        }
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
            "human", invitationId is null ? "access.invitation.created" : "access.invitation.resent",
            "invitation", targetId.ToString("D"),
            invitationId is null ? request!.Reason!.Trim() : "invitation-resend",
            cancellationToken, afterJson: JsonSerializer.Serialize(new { invitationId = targetId,
                roleIds, groupIds }), acr: user.Acr);
        await transaction.CommitAsync(cancellationToken);

        await deliveryCoordinator.DeliverAsync(targetId, tenantId, email, token.Value,
            keycloakUserId, new IdentityAdminActor(actorId, user.Acr), cancellationToken);
        return new(InvitationIssuanceStatus.Accepted);

        async Task<InvitationIssuanceResult> DenyAsync(InvitationIssuanceStatus status, string reason)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", invitationId is null ? "access.invitation.create.denied"
                    : "access.invitation.resend.denied", "invitation",
                invitationId?.ToString("D") ?? "new", reason, cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(status);
        }

        async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = Command(sql, parameters);
            return await command.ExecuteScalarAsync(cancellationToken);
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

    private static InvitationIssuanceStatus MapDecision(RoleAssignmentStatus decision) => decision switch
    {
        RoleAssignmentStatus.Forbidden => InvitationIssuanceStatus.Forbidden,
        RoleAssignmentStatus.StepUpRequired => InvitationIssuanceStatus.StepUpRequired,
        _ => InvitationIssuanceStatus.Conflict
    };

    private static bool ValidRequest(CreateInvitationRequest? request)
    {
        var email = request?.Email?.Trim();
        var roles = request?.RoleIds;
        var groups = request?.GroupIds ?? [];
        return email is { Length: >= 3 and <= 254 } &&
            MailAddress.TryCreate(email, out var address) && address.Address == email &&
            roles is { Length: >= 1 and <= 25 } && roles.All(id => id != Guid.Empty) &&
            roles.Distinct().Count() == roles.Length &&
            groups.Length <= 25 && groups.All(id => id != Guid.Empty) &&
            groups.Distinct().Count() == groups.Length &&
            AccessRoleGrantPolicy.ValidReasonCode(request!.Reason?.Trim());
    }
}
