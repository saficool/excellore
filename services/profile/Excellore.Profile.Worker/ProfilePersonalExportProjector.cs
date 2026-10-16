using System.Text.Json;
using Excellore.Contracts;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Profile.Worker;

public sealed class ProfilePersonalExportProjector(ProfileWorkerDatabase database,
    PostgresEventInbox inbox, IPrivateExportStore objects)
{
    public async Task<bool> BuildProfileAsync(ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var envelope = ProfileExportRequestedV1.Parse(body.Span);
        var data = envelope.Data!;
        await using var connection = await database.Source.OpenConnectionAsync(cancellationToken);
        return await inbox.RunOnceAsync(connection, envelope.Id, null, async (transaction, token) =>
        {
            await SetExportUserAsync(connection, (NpgsqlTransaction)transaction, data.UserId, token);
            await using var command = new NpgsqlCommand("""
                SELECT jsonb_build_object(
                  'profile',(SELECT to_jsonb(p) - 'avatar_object_key' FROM (
                    SELECT user_id,display_name,time_zone,mirror_first_name,mirror_last_name,
                      mirror_locale,created_at,updated_at FROM user_profiles WHERE user_id=@user) p),
                  'preferences',COALESCE((SELECT jsonb_object_agg(key,value)
                    FROM user_preferences WHERE user_id=@user),'{}'::jsonb),
                  'tenantProfiles',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY tenant_id)
                    FROM (SELECT tenant_id,user_id,job_title,department,work_phone,
                      directory_visibility,status,created_at,updated_at
                      FROM tenant_member_profiles WHERE user_id=@user) t),'[]'::jsonb))::text
                """, connection, (NpgsqlTransaction)transaction);
            command.Parameters.AddWithValue("user", data.UserId);
            var json = (string?)await command.ExecuteScalarAsync(token)
                ?? throw new InvalidOperationException("Profile export source unavailable.");
            await objects.PutAsync(data.ExportId, "profile", System.Text.Encoding.UTF8.GetBytes(json), token);
            await using var update = new NpgsqlCommand("""
                UPDATE data_export_jobs SET status='processing'
                WHERE id=@id AND user_id=@user AND status='pending'
                """, connection, (NpgsqlTransaction)transaction);
            update.Parameters.AddWithValue("id", data.ExportId);
            update.Parameters.AddWithValue("user", data.UserId);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidPlatformEventException();
        }, cancellationToken);
    }

    public async Task<bool> CompleteAsync(ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var envelope = AccessUserExportReadyV1.Parse(body.Span);
        var data = envelope.Data!;
        await using var connection = await database.Source.OpenConnectionAsync(cancellationToken);
        return await inbox.RunOnceAsync(connection, envelope.Id, null, async (transaction, token) =>
        {
            await using var lockJob = new NpgsqlCommand("""
                SELECT 1 FROM data_export_jobs WHERE id=@id AND user_id=@user
                  AND status='processing' FOR UPDATE
                """, connection, (NpgsqlTransaction)transaction);
            lockJob.Parameters.AddWithValue("id", data.ExportId);
            lockJob.Parameters.AddWithValue("user", data.UserId);
            if (await lockJob.ExecuteScalarAsync(token) is null)
                throw new InvalidPlatformEventException();
            var profile = await objects.GetAsync(data.ExportId, "profile", token)
                ?? throw new InvalidOperationException("Profile export contribution unavailable.");
            var access = await objects.GetAsync(data.ExportId, "access", token)
                ?? throw new InvalidOperationException("Access export contribution unavailable.");
            using var profileJson = JsonDocument.Parse(profile);
            using var accessJson = JsonDocument.Parse(access);
            var complete = JsonSerializer.SerializeToUtf8Bytes(new
            {
                exportId = data.ExportId,
                generatedAt = DateTimeOffset.UtcNow,
                profile = profileJson.RootElement,
                access = accessJson.RootElement,
                auditRecords = new { included = false, reason = "restricted-multi-subject-records" }
            });
            await objects.PutAsync(data.ExportId, "complete", complete, token);
            await using (var update = new NpgsqlCommand("""
                UPDATE data_export_jobs SET status='ready',object_key=@key,
                  completed_at=now(),expires_at=now()+interval '24 hours'
                WHERE id=@id AND user_id=@user AND status='processing'
                """, connection, (NpgsqlTransaction)transaction))
            {
                update.Parameters.AddWithValue("id", data.ExportId);
                update.Parameters.AddWithValue("user", data.UserId);
                update.Parameters.AddWithValue("key", $"exports/{data.ExportId:D}/complete.json");
                if (await update.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("Export job state changed.");
            }
            var eventId = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(new
            {
                specversion="1.0", id=eventId, type=ProfileExportReadyV1.EventType,
                source="excellore/profile", time=DateTimeOffset.UtcNow,
                correlationid=envelope.CorrelationId,
                actor=new { sub=envelope.Actor!.Sub, type="machine" },
                data=new { userId=data.UserId, exportId=data.ExportId }
            });
            await using var outbox = new NpgsqlCommand("""
                INSERT INTO outbox(id,type,payload) VALUES (@id,@type,@payload::jsonb)
                """, connection, (NpgsqlTransaction)transaction);
            outbox.Parameters.AddWithValue("id", eventId);
            outbox.Parameters.AddWithValue("type", ProfileExportReadyV1.EventType);
            outbox.Parameters.AddWithValue("payload", payload);
            await outbox.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }

    private static async Task SetExportUserAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = new NpgsqlCommand("""
            SELECT set_config('app.export_user_id',@user,true),
                   set_config('app.export_mode','personal',true)
            """, connection, transaction);
        scope.Parameters.AddWithValue("user", userId.ToString("D"));
        await scope.ExecuteNonQueryAsync(cancellationToken);
    }
}
