using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public enum AuditExportDownloadStatus { Ready, NotFound, StepUpRequired }
public sealed record AuditExportDownloadResult(AuditExportDownloadStatus Status, byte[]? Data = null);

/// <summary>Looks up a ready object only in the request's gated tenant context.</summary>
public sealed class AuditExportDownloadService(NpgsqlDataSource database,
    ITenantContextAccessor tenant, ICurrentUser currentUser,
    IAuditExportObjectStore objects)
{
    public async Task<AuditExportDownloadResult> DownloadAsync(Guid exportId,
        CancellationToken cancellationToken = default)
    {
        if (exportId == Guid.Empty) return new(AuditExportDownloadStatus.NotFound);
        if (!currentUser.HasRecentMfa) return new(AuditExportDownloadStatus.StepUpRequired);
        string? key;
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction))
            {
                scope.Parameters.AddWithValue("tenant", tenant.TenantId.ToString("D"));
                await scope.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var lookup = new NpgsqlCommand("""
                SELECT object_key FROM audit_exports
                WHERE tenant_id=@tenant AND id=@id AND status='ready'
                """, connection, transaction);
            lookup.Parameters.AddWithValue("tenant", tenant.TenantId);
            lookup.Parameters.AddWithValue("id", exportId);
            key = await lookup.ExecuteScalarAsync(cancellationToken) as string;
            await transaction.CommitAsync(cancellationToken);
        }
        if (key is null) return new(AuditExportDownloadStatus.NotFound);
        var data = await objects.GetAsync(key, cancellationToken);
        return data is null ? new(AuditExportDownloadStatus.NotFound) :
            new(AuditExportDownloadStatus.Ready, data);
    }
}
