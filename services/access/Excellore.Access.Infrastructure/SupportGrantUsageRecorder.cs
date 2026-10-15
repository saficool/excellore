using System.Text.Json;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed class SupportGrantUsageRecorder(NpgsqlDataSource dataSource) : ISupportGrantUsageRecorder
{
    public async Task RecordIfSupportAsync(Guid tenantId, Guid userId, string method,
        string routePattern, string? acr, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var set = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, tx))
        {
            set.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await set.ExecuteNonQueryAsync(cancellationToken);
        }
        Guid? grantId;
        await using (var command = new NpgsqlCommand("""
            SELECT m.support_grant_id FROM tenant_members m
            JOIN support_grants g ON g.id = m.support_grant_id
              AND g.tenant_id = m.tenant_id AND g.staff_user_id = m.user_id
            WHERE m.tenant_id = @tenant AND m.user_id = @user
              AND m.status = 'active' AND m.is_support AND g.status = 'active'
              AND m.expires_at > now() AND g.expires_at > now()
            """, connection, tx))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("user", userId);
            grantId = await command.ExecuteScalarAsync(cancellationToken) as Guid?;
        }
        if (grantId is null) return;
        await AccessAuditWriter.WriteAsync(connection, tx, tenantId, userId, "human",
            "access.supportgrant.used", "support-grant", grantId.Value.ToString("D"),
            "support-access", cancellationToken,
            afterJson: JsonSerializer.Serialize(new { method, routePattern }), acr: acr);
        await tx.CommitAsync(cancellationToken);
    }
}
