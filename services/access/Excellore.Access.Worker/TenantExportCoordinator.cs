using System.Text;
using System.Text.Json;
using Excellore.Contracts;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Access.Worker;

public sealed class TenantExportCoordinator(AccessWorkerDatabase database,
    PostgresEventInbox inbox, IPrivateExportStore objects, IConfiguration configuration)
{
    public async Task<bool> BuildAccessAsync(ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var envelope = TenantExportRequestedV1.Parse(body.Span); var data = envelope.Data!;
        await using var connection = await database.Source.OpenConnectionAsync(ct);
        return await inbox.RunOnceAsync(connection, envelope.Id, data.TenantId, async (transaction, token) =>
        {
            await using var command = new NpgsqlCommand("""
              SELECT jsonb_build_object(
                'tenant',(SELECT to_jsonb(t) FROM (SELECT id,slug,name,status,plan,region,created_at,updated_at FROM tenants WHERE id=@tenant)t),
                'members',COALESCE((SELECT jsonb_agg(to_jsonb(x) ORDER BY user_id) FROM (SELECT user_id,status,joined_at,updated_at FROM tenant_members WHERE tenant_id=@tenant)x),'[]'::jsonb),
                'roles',COALESCE((SELECT jsonb_agg(to_jsonb(x) ORDER BY id) FROM (SELECT id,name,description,is_system,privileged,created_at,updated_at FROM roles WHERE tenant_id=@tenant)x),'[]'::jsonb),
                'assignments',COALESCE((SELECT jsonb_agg(to_jsonb(x) ORDER BY id) FROM (SELECT id,user_id,role_id,expires_at,revoked_at,created_at FROM role_assignments WHERE tenant_id=@tenant)x),'[]'::jsonb),
                'apps',COALESCE((SELECT jsonb_agg(to_jsonb(x) ORDER BY app_id) FROM (SELECT app_id,status,features,updated_at FROM tenant_apps WHERE tenant_id=@tenant)x),'[]'::jsonb),
                'settings',(SELECT to_jsonb(x) FROM (SELECT * FROM tenant_settings WHERE tenant_id=@tenant)x),
                'audit',COALESCE((SELECT jsonb_agg(to_jsonb(x) ORDER BY occurred_at,id) FROM (SELECT id,occurred_at,actor_sub,actor_type,action,target_type,target_id,before,after,reason,acr,correlation_id FROM audit_events WHERE tenant_id=@tenant)x),'[]'::jsonb))::text
              """, connection, (NpgsqlTransaction)transaction);
            command.Parameters.AddWithValue("tenant", data.TenantId);
            var json = (string?)await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Access tenant export unavailable.");
            await objects.PutAsync(data.ExportId, "access", Encoding.UTF8.GetBytes(json), token);
        }, ct);
    }

    public async Task<bool> CompleteAsync(ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var envelope = ProfileTenantExportReadyV1.Parse(body.Span); var data = envelope.Data!;
        if (!Guid.TryParse(configuration["Worker:ActorId"], out var actor) || actor == Guid.Empty)
            throw new InvalidOperationException("Worker:ActorId must be a machine identity UUID.");
        await using var connection = await database.Source.OpenConnectionAsync(ct);
        return await inbox.RunOnceAsync(connection, envelope.Id, data.TenantId, async (transaction, token) =>
        {
            await using var locked = new NpgsqlCommand("SELECT 1 FROM tenant_exports WHERE tenant_id=@tenant AND id=@id AND status='pending' FOR UPDATE", connection, (NpgsqlTransaction)transaction);
            locked.Parameters.AddWithValue("tenant", data.TenantId); locked.Parameters.AddWithValue("id", data.ExportId);
            if (await locked.ExecuteScalarAsync(token) is null) throw new InvalidPlatformEventException();
            var access = await objects.GetAsync(data.ExportId, "access", token) ?? throw new InvalidOperationException("Access tenant contribution unavailable.");
            var profile = await objects.GetAsync(data.ExportId, "profile", token) ?? throw new InvalidOperationException("Profile tenant contribution unavailable.");
            using var accessJson = JsonDocument.Parse(access); using var profileJson = JsonDocument.Parse(profile);
            var complete = JsonSerializer.SerializeToUtf8Bytes(new { exportId = data.ExportId, tenantId = data.TenantId, generatedAt = DateTimeOffset.UtcNow, access = accessJson.RootElement, profile = profileJson.RootElement });
            await objects.PutAsync(data.ExportId, "complete", complete, token);
            await using var update = new NpgsqlCommand("UPDATE tenant_exports SET status='ready',object_key=@key,ready_at=now() WHERE tenant_id=@tenant AND id=@id AND status='pending'", connection, (NpgsqlTransaction)transaction);
            update.Parameters.AddWithValue("key", $"exports/{data.ExportId:D}/complete.json"); update.Parameters.AddWithValue("tenant", data.TenantId); update.Parameters.AddWithValue("id", data.ExportId);
            if (await update.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("Tenant export state changed.");
            await Excellore.Access.Infrastructure.TenantExportCompletionAudit.WriteAsync(
                connection, (NpgsqlTransaction)transaction, data.TenantId, actor, data.ExportId, token);
        }, ct);
    }
}
