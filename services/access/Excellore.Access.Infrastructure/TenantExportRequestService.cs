using Excellore.Auth;
using Excellore.Authz;
using Npgsql;
using System.Text.Json;
using Excellore.Contracts;

namespace Excellore.Access.Infrastructure;

public enum TenantExportRequestStatus { Accepted, Forbidden, StepUpRequired, NotFound, DailyLimit }
public sealed record TenantExportRequestResult(TenantExportRequestStatus Status, Guid? ExportId = null);
public sealed record TenantExportView(Guid Id, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt);

public sealed class TenantExportRequestService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenant, ICurrentUser user, AccessResolutionService resolver)
{
    public async Task<TenantExportRequestResult> RequestAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.TenantId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            guard.Parameters.AddWithValue("key", AccessAuditWriter.AuditLock);
            await guard.ExecuteNonQueryAsync(cancellationToken);
        }
        await SetTenantAsync(connection, transaction, tenantId, cancellationToken);
        var resolution = await resolver.ResolveAsync(user.UserId, tenantId, cancellationToken);
        TenantExportRequestStatus? denied = null;
        string? reason = null;
        if (!resolution.Allowed)
            (denied, reason) = (TenantExportRequestStatus.NotFound, "membership-inactive");
        else if (!resolution.Roles.Contains("owner", StringComparer.Ordinal) ||
                 !resolution.Permissions.Contains("access.tenant.export", StringComparer.Ordinal))
            (denied, reason) = (TenantExportRequestStatus.Forbidden, "owner-required");
        else if (!user.HasRecentMfa)
            (denied, reason) = (TenantExportRequestStatus.StepUpRequired, "step-up-required");
        if (denied.HasValue)
        {
            await AuditAsync("access.tenant.export-request.denied", "denied", reason!);
            return new(denied.Value);
        }
        var id = Guid.NewGuid();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO tenant_exports(id, tenant_id, requested_by)
            SELECT @id, @tenant, @actor FROM tenants
            WHERE id=@tenant AND status='active'
            ON CONFLICT ON CONSTRAINT tenant_exports_daily_limit DO NOTHING
            RETURNING id
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("tenant", tenantId);
        insert.Parameters.AddWithValue("actor", user.UserId);
        var inserted = await insert.ExecuteScalarAsync(cancellationToken) as Guid?;
        if (!inserted.HasValue)
        {
            var active = await TenantActiveAsync(connection, transaction, tenantId, cancellationToken);
            var status = active ? TenantExportRequestStatus.DailyLimit : TenantExportRequestStatus.NotFound;
            await AuditAsync("access.tenant.export-request.denied", "denied",
                active ? "daily-limit" : "tenant-inactive");
            return new(status);
        }
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0",
            id,
            type = TenantExportRequestedV1.EventType,
            source = TenantExportRequestedV1.EventSource,
            time = DateTimeOffset.UtcNow,
            tenantid = tenantId,
            correlationid = id,
            actor = new { sub = user.UserId.ToString("D"), type = "human" },
            data = new { tenantId, exportId = id }
        });
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO outbox(id,type,tenant_id,payload) VALUES (@id,@type,@tenant,@payload::jsonb)
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("id", id);
            outbox.Parameters.AddWithValue("type", TenantExportRequestedV1.EventType);
            outbox.Parameters.AddWithValue("tenant", tenantId);
            outbox.Parameters.AddWithValue("payload", payload);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync("access.tenant.export-requested", id.ToString("D"), "tenant-export");
        return new(TenantExportRequestStatus.Accepted, id);

        async Task AuditAsync(string action, string target, string auditReason)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, user.UserId,
                "human", action, "tenant-export", target, auditReason, cancellationToken,
                acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<TenantExportView?> GetAsync(Guid exportId,
        CancellationToken cancellationToken = default)
    {
        if (exportId == Guid.Empty) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, tenant.TenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id,status,created_at,ready_at FROM tenant_exports
            WHERE tenant_id=@tenant AND id=@id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant.TenantId);
        command.Parameters.AddWithValue("id", exportId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TenantExportView(reader.GetGuid(0), reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)) : null;
    }

    private static async Task<bool> TenantActiveAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var check = new NpgsqlCommand(
            "SELECT status='active' FROM tenants WHERE id=@tenant", connection, transaction);
        check.Parameters.AddWithValue("tenant", tenantId);
        return await check.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction);
        scope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
        await scope.ExecuteNonQueryAsync(cancellationToken);
    }
}
