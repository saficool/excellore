using System.Text;
using System.Text.Json;
using Excellore.Contracts;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Access.Worker;

public sealed class PersonalExportContributor(AccessWorkerDatabase database,
    PostgresEventInbox inbox, IPrivateExportStore objects, IConfiguration configuration)
{
    public async Task<bool> ApplyAsync(ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var envelope = ProfileExportRequestedV1.Parse(body.Span);
        var data = envelope.Data!;
        if (!Guid.TryParse(configuration["Worker:ActorId"], out var machineActor) ||
            machineActor == Guid.Empty)
            throw new InvalidOperationException("Worker:ActorId must be a machine identity UUID.");
        await using var connection = await database.Source.OpenConnectionAsync(cancellationToken);
        return await inbox.RunOnceAsync(connection, envelope.Id, null, async (transaction, token) =>
        {
            await using var query = new NpgsqlCommand("""
                SELECT jsonb_build_object('memberships',COALESCE(jsonb_agg(
                  jsonb_build_object('tenantId',tenant_id,'status',membership_status)
                  ORDER BY tenant_id),'[]'::jsonb))::text
                FROM user_tenant_index WHERE user_id=@user
                """, connection, (NpgsqlTransaction)transaction);
            query.Parameters.AddWithValue("user", data.UserId);
            var json = (string?)await query.ExecuteScalarAsync(token)
                ?? throw new InvalidOperationException("Access export source unavailable.");
            await objects.PutAsync(data.ExportId, "access", Encoding.UTF8.GetBytes(json), token);
            var eventId = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(new
            {
                specversion="1.0", id=eventId, type=AccessUserExportReadyV1.EventType,
                source=AccessUserExportReadyV1.EventSource, time=DateTimeOffset.UtcNow,
                correlationid=envelope.CorrelationId,
                actor=new { sub=machineActor.ToString("D"), type="machine" },
                data=new { userId=data.UserId, exportId=data.ExportId }
            });
            await using var outbox = new NpgsqlCommand("""
                INSERT INTO outbox(id,type,tenant_id,payload)
                VALUES (@id,@type,NULL,@payload::jsonb)
                """, connection, (NpgsqlTransaction)transaction);
            outbox.Parameters.AddWithValue("id", eventId);
            outbox.Parameters.AddWithValue("type", AccessUserExportReadyV1.EventType);
            outbox.Parameters.AddWithValue("payload", payload);
            await outbox.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }
}
