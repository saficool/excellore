using Excellore.Access.Infrastructure;
using Excellore.Contracts;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Access.Worker;

/// <summary>Records only a confirmation for a deletion Access already dispatched.</summary>
public sealed class ProfileErasureConfirmationProjector(AccessWorkerDatabase database,
    PostgresEventInbox inbox)
{
    public async Task<bool> ApplyAsync(ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var envelope = ProfileErasureConfirmedV1.Parse(body.Span);
        if (envelope.Data!.Scope != "tenant")
            throw new InvalidPlatformEventException();
        var tenantId = envelope.TenantId!.Value;
        await using var connection = await database.Source.OpenConnectionAsync(cancellationToken);
        return await inbox.RunOnceAsync(connection, envelope.Id, tenantId,
            async (transaction, token) =>
            {
                await using var check = new NpgsqlCommand("""
                    SELECT 1 FROM tenants WHERE id=@tenant
                    AND status='pending_deletion' AND deletion_started_at IS NOT NULL
                    """, connection, (NpgsqlTransaction)transaction);
                check.Parameters.AddWithValue("tenant", tenantId);
                if (await check.ExecuteScalarAsync(token) is null)
                    throw new InvalidPlatformEventException();
                await using var receipt = new NpgsqlCommand("""
                    INSERT INTO tenant_tombstones(tenant_id,profile_confirmed_at)
                    VALUES (@tenant,now())
                    ON CONFLICT (tenant_id) DO UPDATE SET
                      profile_confirmed_at=COALESCE(tenant_tombstones.profile_confirmed_at,
                        EXCLUDED.profile_confirmed_at)
                    """, connection, (NpgsqlTransaction)transaction);
                receipt.Parameters.AddWithValue("tenant", tenantId);
                await receipt.ExecuteNonQueryAsync(token);
            }, cancellationToken);
    }
}
