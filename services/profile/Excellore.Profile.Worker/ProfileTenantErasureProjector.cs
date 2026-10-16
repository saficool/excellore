using System.Text.Json;
using Excellore.Contracts;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Profile.Worker;

/// <summary>Erases tenant member data and emits a confirmation in one RLS-scoped transaction.</summary>
public sealed class ProfileTenantErasureProjector(ProfileWorkerDatabase database, PostgresEventInbox inbox)
{
    public async Task<bool> ApplyAsync(ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var envelope = TenantDeletedV1.Parse(body.Span);
        var tenantId = envelope.TenantId!.Value;
        await using var connection = await database.Source.OpenConnectionAsync(cancellationToken);
        return await inbox.RunOnceAsync(connection, envelope.Id, tenantId,
            async (transaction, token) =>
            {
                await using (var serial = new NpgsqlCommand(
                    "SELECT pg_advisory_xact_lock(hashtextextended(@tenant::text,0))",
                    connection, (NpgsqlTransaction)transaction))
                {
                    serial.Parameters.AddWithValue("tenant", tenantId);
                    await serial.ExecuteNonQueryAsync(token);
                }
                await using (var delete = new NpgsqlCommand(
                    "DELETE FROM tenant_member_profiles WHERE tenant_id=@tenant",
                    connection, (NpgsqlTransaction)transaction))
                {
                    delete.Parameters.AddWithValue("tenant", tenantId);
                    await delete.ExecuteNonQueryAsync(token);
                }
                await using (var marker = new NpgsqlCommand("""
                    INSERT INTO profile_tenant_tombstones(tenant_id) VALUES (@tenant)
                    ON CONFLICT (tenant_id) DO NOTHING
                    """, connection, (NpgsqlTransaction)transaction))
                {
                    marker.Parameters.AddWithValue("tenant", tenantId);
                    await marker.ExecuteNonQueryAsync(token);
                }
                await using (var scope = new NpgsqlCommand(
                    "SELECT set_config('app.erasure','true',true)",
                    connection, (NpgsqlTransaction)transaction))
                    await scope.ExecuteNonQueryAsync(token);
                await using (var archive = new NpgsqlCommand("""
                    INSERT INTO profile_audit_archive
                    SELECT * FROM profile_audit_events WHERE tenant_id=@tenant
                    """, connection, (NpgsqlTransaction)transaction))
                {
                    archive.Parameters.AddWithValue("tenant", tenantId);
                    await archive.ExecuteNonQueryAsync(token);
                }
                await using (var deleteAudit = new NpgsqlCommand(
                    "DELETE FROM profile_audit_events WHERE tenant_id=@tenant",
                    connection, (NpgsqlTransaction)transaction))
                {
                    deleteAudit.Parameters.AddWithValue("tenant", tenantId);
                    await deleteAudit.ExecuteNonQueryAsync(token);
                }
                var confirmationId = Guid.NewGuid();
                var payload = JsonSerializer.Serialize(new
                {
                    specversion = "1.0", id = confirmationId,
                    type = ProfileErasureConfirmedV1.EventType,
                    source = ProfileErasureConfirmedV1.EventSource,
                    time = DateTimeOffset.UtcNow, tenantid = tenantId,
                    correlationid = envelope.CorrelationId,
                    actor = new { sub = envelope.Actor!.Sub, type = "machine" },
                    data = new { scope = "tenant", tenantId, service = "profile" }
                });
                await using var outbox = new NpgsqlCommand("""
                    INSERT INTO outbox(id,type,tenant_id,payload)
                    VALUES (@id,@type,@tenant,@payload::jsonb)
                    """, connection, (NpgsqlTransaction)transaction);
                outbox.Parameters.AddWithValue("id", confirmationId);
                outbox.Parameters.AddWithValue("type", ProfileErasureConfirmedV1.EventType);
                outbox.Parameters.AddWithValue("tenant", tenantId);
                outbox.Parameters.AddWithValue("payload", payload);
                await outbox.ExecuteNonQueryAsync(token);
            }, cancellationToken);
    }
}
