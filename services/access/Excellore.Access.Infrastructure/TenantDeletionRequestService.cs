using System.Text.Json;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record RequestTenantDeletion(string? Reason);
public enum TenantDeletionRequestStatus { Accepted, Invalid, Forbidden, StepUpRequired, NotFound, Conflict }
public sealed record TenantDeletionRequestResult(TenantDeletionRequestStatus Status, DateTimeOffset? DueAt = null);

public sealed class TenantDeletionRequestService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext, ICurrentUser user, AccessResolutionService resolver)
{
    public async Task<TenantDeletionRequestResult> RequestAsync(RequestTenantDeletion? request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId;
        var actorId = user.UserId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            guard.Parameters.AddWithValue("key", AccessAuditWriter.AuditLock);
            await guard.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var setTenant = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            setTenant.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await setTenant.ExecuteNonQueryAsync(cancellationToken);
        }
        string? status;
        await using (var check = new NpgsqlCommand(
            "SELECT status FROM tenants WHERE id = @tenant FOR UPDATE", connection, transaction))
        {
            check.Parameters.AddWithValue("tenant", tenantId);
            status = await check.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (status is null)
            return await DenyAsync(TenantDeletionRequestStatus.NotFound, "tenant-unavailable");
        if (status != "active")
            return await DenyAsync(TenantDeletionRequestStatus.Conflict, "tenant-inactive");
        var current = await resolver.ResolveAsync(actorId, tenantId, cancellationToken);
        if (!current.Allowed)
            return await DenyAsync(TenantDeletionRequestStatus.NotFound, "membership-inactive");
        if (!current.Roles.Contains("owner", StringComparer.Ordinal) ||
            !current.Permissions.Contains("access.tenant.delete", StringComparer.Ordinal))
            return await DenyAsync(TenantDeletionRequestStatus.Forbidden, "owner-required");
        if (!user.HasRecentMfa)
            return await DenyAsync(TenantDeletionRequestStatus.StepUpRequired, "step-up-required");
        var reason = request?.Reason?.Trim();
        if (!AccessRoleGrantPolicy.ValidReasonCode(reason))
            return await DenyAsync(TenantDeletionRequestStatus.Invalid, "invalid-reason");
        DateTimeOffset dueAt;
        await using (var update = new NpgsqlCommand("""
            UPDATE tenants SET status = 'pending_deletion',
              deletion_requested_at = now(), deletion_due_at = now() + interval '14 days',
              updated_at = now()
            WHERE id = @tenant RETURNING deletion_due_at
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Tenant unavailable.");
            dueAt = reader.GetFieldValue<DateTimeOffset>(0);
        }
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
            "human", "access.tenant.deletion-requested", "tenant", tenantId.ToString("D"),
            reason!, cancellationToken,
            beforeJson: JsonSerializer.Serialize(new { status = "active" }),
            afterJson: JsonSerializer.Serialize(new { status = "pending_deletion", dueAt }),
            acr: user.Acr);
        await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction,
            tenantId, actorId, cancellationToken);
        var eventId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0", id = eventId, type = "tenant.deletion.requested.v1",
            source = "excellore/access", time = DateTimeOffset.UtcNow,
            tenantid = tenantId, correlationid = Guid.NewGuid(),
            actor = new { sub = actorId.ToString("D"), type = "human" },
            data = new { tenantId, dueAt }
        });
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO outbox (id, type, tenant_id, payload)
            VALUES (@id, 'tenant.deletion.requested.v1', @tenant, @payload::jsonb)
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("id", eventId);
            outbox.Parameters.AddWithValue("tenant", tenantId);
            outbox.Parameters.AddWithValue("payload", payload);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(TenantDeletionRequestStatus.Accepted, dueAt);

        async Task<TenantDeletionRequestResult> DenyAsync(TenantDeletionRequestStatus denied, string code)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", "access.tenant.deletion-request.denied", "tenant",
                tenantId.ToString("D"), code, cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(denied);
        }
    }
}
