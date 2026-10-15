using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record AuditExportView(Guid Id, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt);
public enum AuditExportRequestStatus { Accepted, Forbidden, StepUpRequired, NotFound, Conflict }
public sealed record AuditExportRequestResult(AuditExportRequestStatus Status, Guid? ExportId = null);

public sealed class AuditExportRequestService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext, ICurrentUser user, AccessResolutionService resolver)
{
    public async Task<AuditExportView?> GetAsync(Guid exportId,
        CancellationToken cancellationToken = default)
    {
        if (exportId == Guid.Empty) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, status, created_at, ready_at FROM audit_exports
            WHERE tenant_id = @tenant AND id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId);
        command.Parameters.AddWithValue("id", exportId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AuditExportView(reader.GetGuid(0), reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)) : null;
    }

    public async Task<AuditExportRequestResult> RequestAsync(
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
        await SetTenantAsync(connection, transaction, cancellationToken);
        string? tenantStatus;
        await using (var command = new NpgsqlCommand(
            "SELECT status FROM tenants WHERE id = @tenant FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            tenantStatus = await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (tenantStatus != "active")
            return await DenyAsync(AuditExportRequestStatus.NotFound, "tenant-inactive");
        var current = await resolver.ResolveAsync(actorId, tenantId, cancellationToken);
        if (!current.Allowed)
            return await DenyAsync(AuditExportRequestStatus.NotFound, "membership-inactive");
        if (!current.Permissions.Contains("access.audit.read", StringComparer.Ordinal))
            return await DenyAsync(AuditExportRequestStatus.Forbidden, "permission-missing");
        if (!user.HasRecentMfa)
            return await DenyAsync(AuditExportRequestStatus.StepUpRequired, "step-up-required");
        await using (var check = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM audit_exports
              WHERE tenant_id = @tenant AND requested_by = @actor AND status = 'pending')
            """, connection, transaction))
        {
            check.Parameters.AddWithValue("tenant", tenantId);
            check.Parameters.AddWithValue("actor", actorId);
            if (await check.ExecuteScalarAsync(cancellationToken) is true)
                return await DenyAsync(AuditExportRequestStatus.Conflict, "export-pending");
        }
        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO audit_exports (id, tenant_id, requested_by, status)
            VALUES (@id, @tenant, @actor, 'pending')
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("actor", actorId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
            "human", "access.audit.export-requested", "audit-export", id.ToString("D"),
            "audit-export", cancellationToken, acr: user.Acr);
        await transaction.CommitAsync(cancellationToken);
        return new(AuditExportRequestStatus.Accepted, id);

        async Task<AuditExportRequestResult> DenyAsync(AuditExportRequestStatus denied, string code)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", "access.audit.export-request.denied", "audit-export", "new",
                code, cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(denied);
        }
    }

    private async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
