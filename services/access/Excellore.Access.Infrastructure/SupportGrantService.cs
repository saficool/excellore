using System.Text.Json;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record CreateSupportGrantRequest(Guid TenantId, Guid RoleId, int DurationMinutes, string? Reason);
public sealed record DecideSupportGrantRequest(string? Reason);
public sealed record SupportGrantView(Guid Id, Guid TenantId, Guid StaffUserId, Guid RoleId,
    string StaffEmail, string RoleName, string Reason, string Status,
    DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);
public enum SupportGrantStatus { Created, Approved, Revoked, Invalid, Forbidden, StepUpRequired, NotFound, Conflict }
public sealed record SupportGrantResult(SupportGrantStatus Status, Guid? GrantId = null);

public sealed class SupportGrantService(NpgsqlDataSource dataSource, ICurrentUser user,
    ITenantContextAccessor tenantContext, AccessResolutionService resolver)
{
    public async Task<SupportGrantResult> RequestAsync(CreateSupportGrantRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.TenantId == Guid.Empty || request.RoleId == Guid.Empty ||
            request.DurationMinutes is < 1 or > 480 ||
            !AccessRoleGrantPolicy.ValidReasonCode(request.Reason?.Trim()))
            return new(SupportGrantStatus.Invalid);
        if (!user.HasRecentMfa) return new(SupportGrantStatus.StepUpRequired);
        var tenantId = request.TenantId;
        var actor = user.UserId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await LockAndScopeAsync(connection, tx, tenantId, cancellationToken);
        if (await ScalarAsync(connection, tx, "SELECT status FROM tenants WHERE id = @tenant",
            cancellationToken, ("tenant", tenantId)) as string != "active")
            return new(SupportGrantStatus.NotFound);
        if (await ScalarAsync(connection, tx, """
            SELECT EXISTS(SELECT 1 FROM roles WHERE id = @role AND tenant_id IS NULL
              AND name IN ('viewer','auditor') AND deleted_at IS NULL)
            """, cancellationToken, ("role", request.RoleId)) is not true)
            return new(SupportGrantStatus.Invalid);
        if (await ScalarAsync(connection, tx,
            "SELECT EXISTS(SELECT 1 FROM identities WHERE user_id = @actor AND status = 'active')",
            cancellationToken, ("actor", actor)) is not true)
            return new(SupportGrantStatus.NotFound);
        if (await ScalarAsync(connection, tx, """
            SELECT EXISTS(SELECT 1 FROM tenant_members
              WHERE tenant_id = @tenant AND user_id = @actor
                AND (status <> 'removed' OR NOT is_support))
            """, cancellationToken, ("tenant", tenantId), ("actor", actor)) is true)
            return new(SupportGrantStatus.Conflict);
        if (await ScalarAsync(connection, tx, """
            SELECT EXISTS(SELECT 1 FROM support_grants
              WHERE tenant_id = @tenant AND staff_user_id = @actor
                AND status IN ('requested','approved','active'))
            """, cancellationToken, ("tenant", tenantId), ("actor", actor)) is true)
            return new(SupportGrantStatus.Conflict);
        var id = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(request.DurationMinutes);
        await ExecuteAsync(connection, tx, """
            INSERT INTO support_grants (id, tenant_id, staff_user_id, role_id, reason, status, expires_at)
            VALUES (@id, @tenant, @actor, @role, @reason, 'requested', @expires)
            """, cancellationToken, ("id", id), ("tenant", tenantId), ("actor", actor),
            ("role", request.RoleId), ("reason", request.Reason!.Trim()), ("expires", expiresAt));
        await AccessAuditWriter.WriteAsync(connection, tx, tenantId, actor, "human",
            "access.supportgrant.requested", "support-grant", id.ToString("D"),
            request.Reason.Trim(), cancellationToken,
            afterJson: JsonSerializer.Serialize(new { staffUserId = actor, roleId = request.RoleId,
                expiresAt, status = "requested" }), acr: user.Acr);
        await tx.CommitAsync(cancellationToken);
        return new(SupportGrantStatus.Created, id);
    }

    public async Task<IReadOnlyList<SupportGrantView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await ScopeAsync(connection, tx, tenantId, cancellationToken);
        await using var command = Command(connection, tx, """
            SELECT g.id, g.tenant_id, g.staff_user_id, g.role_id,
              i.email, r.name, g.reason, g.status, g.expires_at, g.created_at
            FROM support_grants g
            JOIN identities i ON i.user_id = g.staff_user_id
            JOIN roles r ON r.id = g.role_id
            WHERE g.tenant_id = @tenant ORDER BY g.created_at DESC LIMIT 100
            """, ("tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<SupportGrantView>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetGuid(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        return items;
    }

    public Task<SupportGrantResult> ApproveAsync(Guid grantId, DecideSupportGrantRequest? request,
        CancellationToken cancellationToken = default) =>
        DecideAsync(grantId, request, true, cancellationToken);

    public Task<SupportGrantResult> RevokeAsync(Guid grantId, DecideSupportGrantRequest? request,
        CancellationToken cancellationToken = default) =>
        DecideAsync(grantId, request, false, cancellationToken);

    private async Task<SupportGrantResult> DecideAsync(Guid grantId, DecideSupportGrantRequest? request,
        bool approve, CancellationToken cancellationToken)
    {
        if (grantId == Guid.Empty) return new(SupportGrantStatus.NotFound);
        var reason = request?.Reason?.Trim();
        if (!AccessRoleGrantPolicy.ValidReasonCode(reason)) return new(SupportGrantStatus.Invalid);
        var tenantId = tenantContext.TenantId;
        var actor = user.UserId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await LockAndScopeAsync(connection, tx, tenantId, cancellationToken);
        var current = await resolver.ResolveAsync(actor, tenantId, cancellationToken);
        if (!current.Allowed) return new(SupportGrantStatus.NotFound);
        if (!current.Roles.Contains("owner", StringComparer.Ordinal) ||
            !current.Permissions.Contains("access.supportgrant.manage", StringComparer.Ordinal))
            return new(SupportGrantStatus.Forbidden);
        if (!user.HasRecentMfa) return new(SupportGrantStatus.StepUpRequired);
        Guid staff = Guid.Empty;
        Guid role = Guid.Empty;
        string? status = null;
        DateTimeOffset expires = default;
        await using (var command = Command(connection, tx, """
            SELECT staff_user_id, role_id, status, expires_at FROM support_grants
            WHERE id = @id AND tenant_id = @tenant FOR UPDATE
            """, ("id", grantId), ("tenant", tenantId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                staff = reader.GetGuid(0);
                role = reader.GetGuid(1);
                status = reader.GetString(2);
                expires = reader.GetFieldValue<DateTimeOffset>(3);
            }
        }
        if (status is null) return new(SupportGrantStatus.NotFound);
        if (staff == actor) return new(SupportGrantStatus.Forbidden);
        if (approve)
        {
            if (status != "requested" || expires <= DateTimeOffset.UtcNow)
                return new(SupportGrantStatus.Conflict);
            if (await ScalarAsync(connection, tx, """
                SELECT EXISTS(SELECT 1 FROM identities WHERE user_id = @staff AND status = 'active')
                """, cancellationToken, ("staff", staff)) is not true)
                return new(SupportGrantStatus.Conflict);
            if (await ScalarAsync(connection, tx, """
                SELECT EXISTS(SELECT 1 FROM roles WHERE id = @role AND tenant_id IS NULL
                  AND name IN ('viewer','auditor') AND deleted_at IS NULL)
                """, cancellationToken, ("role", role)) is not true)
                return new(SupportGrantStatus.Conflict);
            if (await ScalarAsync(connection, tx, """
                SELECT EXISTS(SELECT 1 FROM tenant_members
                  WHERE tenant_id = @tenant AND user_id = @staff
                    AND (status <> 'removed' OR NOT is_support))
                """, cancellationToken, ("tenant", tenantId), ("staff", staff)) is true)
                return new(SupportGrantStatus.Conflict);
            await ExecuteAsync(connection, tx, """
                UPDATE support_grants SET status = 'active', approved_by = @actor, starts_at = now()
                WHERE tenant_id = @tenant AND id = @id
                """, cancellationToken, ("tenant", tenantId), ("id", grantId), ("actor", actor));
            await ExecuteAsync(connection, tx, """
                INSERT INTO tenant_members
                  (tenant_id, user_id, status, is_support, support_grant_id, joined_at, expires_at)
                VALUES (@tenant, @staff, 'active', true, @id, now(), @expires)
                ON CONFLICT (tenant_id, user_id) DO UPDATE SET status = 'active',
                  is_support = true, support_grant_id = @id, joined_at = now(),
                  expires_at = @expires, updated_at = now()
                WHERE tenant_members.status = 'removed' AND tenant_members.is_support
                """, cancellationToken, ("tenant", tenantId), ("staff", staff),
                ("id", grantId), ("expires", expires));
            await ExecuteAsync(connection, tx, """
                INSERT INTO user_tenant_index (user_id, tenant_id, membership_status)
                VALUES (@staff, @tenant, 'active')
                ON CONFLICT (user_id, tenant_id) DO UPDATE SET membership_status = 'active'
                """, cancellationToken, ("staff", staff), ("tenant", tenantId));
            await ExecuteAsync(connection, tx, """
                INSERT INTO role_assignments
                  (id, tenant_id, user_id, role_id, expires_at, reason, granted_by)
                VALUES (@id, @tenant, @staff, @role, @expires, @reason, @actor)
                """, cancellationToken, ("id", Guid.NewGuid()), ("tenant", tenantId),
                ("staff", staff), ("role", role), ("expires", expires),
                ("reason", reason!), ("actor", actor));
        }
        else
        {
            if (status is not ("active" or "requested")) return new(SupportGrantStatus.Conflict);
            await ExecuteAsync(connection, tx, """
                UPDATE support_grants SET status = 'revoked'
                WHERE tenant_id = @tenant AND id = @id
                """, cancellationToken, ("tenant", tenantId), ("id", grantId));
            if (status == "active")
            {
                await ExecuteAsync(connection, tx, """
                    UPDATE tenant_members SET status = 'removed', updated_at = now()
                    WHERE tenant_id = @tenant AND user_id = @staff
                      AND is_support AND support_grant_id = @id
                    """, cancellationToken, ("tenant", tenantId), ("staff", staff), ("id", grantId));
                await ExecuteAsync(connection, tx, """
                    UPDATE user_tenant_index SET membership_status = 'removed'
                    WHERE tenant_id = @tenant AND user_id = @staff
                    """, cancellationToken, ("tenant", tenantId), ("staff", staff));
                await ExecuteAsync(connection, tx, """
                    UPDATE role_assignments SET revoked_at = now()
                    WHERE tenant_id = @tenant AND user_id = @staff AND revoked_at IS NULL
                    """, cancellationToken, ("tenant", tenantId), ("staff", staff));
            }
        }
        await AccessAuditWriter.WriteAsync(connection, tx, tenantId, actor, "human",
            approve ? "access.supportgrant.approved" : "access.supportgrant.revoked",
            "support-grant", grantId.ToString("D"), reason!, cancellationToken,
            beforeJson: JsonSerializer.Serialize(new { status }),
            afterJson: JsonSerializer.Serialize(new { staffUserId = staff, roleId = role,
                status = approve ? "active" : "revoked", expiresAt = expires }), acr: user.Acr);
        if (approve || status == "active")
            await AccessAuthzChangeWriter.BumpTenantAsync(connection, tx, tenantId, actor, cancellationToken);
        await WriteEventAsync(connection, tx, tenantId, grantId, staff, expires, actor,
            approve ? "created" : "revoked", cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(approve ? SupportGrantStatus.Approved : SupportGrantStatus.Revoked, grantId);
    }

    private static async Task WriteEventAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid tenantId, Guid grantId, Guid staff, DateTimeOffset expires, Guid actor,
        string verb, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var type = $"access.supportgrant.{verb}.v1";
        var payload = JsonSerializer.Serialize(new { specversion = "1.0", id, type,
            source = "excellore/access", time = DateTimeOffset.UtcNow, tenantid = tenantId,
            correlationid = Guid.NewGuid(), actor = new { sub = actor.ToString("D"), type = "human" },
            data = new { tenantId, grantId, staffUserId = staff, expiresAt = expires } });
        await ExecuteAsync(connection, tx, """
            INSERT INTO outbox (id, type, tenant_id, payload)
            VALUES (@id, @type, @tenant, @payload::jsonb)
            """, cancellationToken, ("id", id), ("type", type), ("tenant", tenantId),
            ("payload", payload));
    }

    private static async Task LockAndScopeAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid tenantId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(@key)", cancellationToken,
            ("key", AccessAuditWriter.AuditLock));
        await ScopeAsync(connection, tx, tenantId, cancellationToken);
    }

    private static Task ScopeAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid tenantId, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, tx, "SELECT set_config('app.tenant_id', @tenant, true)",
            cancellationToken, ("tenant", tenantId.ToString("D")));

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, tx, sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, tx, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction tx,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
