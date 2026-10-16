using System.Text;
using System.Text.Json;
using Excellore.Contracts;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Profile.Worker;

public sealed class ProfileTenantExportProjector(ProfileWorkerDatabase database,
    PostgresEventInbox inbox, IPrivateExportStore objects, IConfiguration configuration)
{
    public async Task<bool> ApplyAsync(ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var envelope = TenantExportRequestedV1.Parse(body.Span); var data = envelope.Data!;
        if (!Guid.TryParse(configuration["Worker:ActorId"], out var actor) || actor == Guid.Empty)
            throw new InvalidOperationException("Worker:ActorId must be a machine identity UUID.");
        await using var connection = await database.Source.OpenConnectionAsync(ct);
        return await inbox.RunOnceAsync(connection, envelope.Id, data.TenantId, async (transaction, token) =>
        {
            await using var query = new NpgsqlCommand("""
              SELECT jsonb_build_object('memberProfiles',COALESCE(jsonb_agg(to_jsonb(x) ORDER BY user_id),'[]'::jsonb))::text
              FROM (SELECT user_id,job_title,department,work_phone,directory_visibility,status,created_at,updated_at
                FROM tenant_member_profiles WHERE tenant_id=@tenant)x
              """, connection, (NpgsqlTransaction)transaction);
            query.Parameters.AddWithValue("tenant", data.TenantId);
            var json = (string?)await query.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Profile tenant export unavailable.");
            await objects.PutAsync(data.ExportId, "profile", Encoding.UTF8.GetBytes(json), token);
            var eventId = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(new { specversion = "1.0", id = eventId, type = ProfileTenantExportReadyV1.EventType, source = ProfileTenantExportReadyV1.EventSource, time = DateTimeOffset.UtcNow, tenantid = data.TenantId, correlationid = envelope.CorrelationId, actor = new { sub = actor.ToString("D"), type = "machine" }, data = new { tenantId = data.TenantId, exportId = data.ExportId } });
            await using var outbox = new NpgsqlCommand("INSERT INTO outbox(id,type,tenant_id,payload) VALUES (@id,@type,@tenant,@payload::jsonb)", connection, (NpgsqlTransaction)transaction);
            outbox.Parameters.AddWithValue("id", eventId); outbox.Parameters.AddWithValue("type", ProfileTenantExportReadyV1.EventType); outbox.Parameters.AddWithValue("tenant", data.TenantId); outbox.Parameters.AddWithValue("payload", payload);
            await outbox.ExecuteNonQueryAsync(token);
        }, ct);
    }
}
