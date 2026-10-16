using System.Text.Json;
using Excellore.Contracts;
using Excellore.Platform;
using Excellore.Profile.Infrastructure;
using Npgsql;

namespace Excellore.Profile.Worker;

/// <summary>Removes Profile personal data after Access has revoked all authorization.</summary>
public sealed class ProfileUserErasureProjector(ProfileWorkerDatabase database,
    PostgresEventInbox inbox, IAvatarObjectStore avatars, IPrivateExportStore exports,
    IConfiguration configuration)
{
    public async Task<bool> ApplyAsync(ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var envelope = AccessErasureConfirmedV1.Parse(body.Span);
        if (envelope.Data!.Scope == "tenant")
            return await RecordTenantConfirmationAsync(envelope, cancellationToken);
        if (envelope.Data.Scope != "user") throw new InvalidPlatformEventException();
        var userId = envelope.Data.UserId!.Value;
        if (!Guid.TryParse(configuration["Worker:ActorId"], out var machineActor) ||
            machineActor == Guid.Empty)
            throw new InvalidOperationException("Worker:ActorId must be a machine identity UUID.");

        await using var connection = await database.Source.OpenConnectionAsync(cancellationToken);
        var (avatarKeys, exportIds) = await ObjectIdsAsync(connection, userId, cancellationToken);
        var tenantIds = await TenantIdsAsync(connection, userId, cancellationToken);
        foreach (var key in avatarKeys)
            await avatars.DeleteAsync(key, cancellationToken);
        foreach (var exportId in exportIds)
            foreach (var part in new[] { "profile", "access", "complete" })
                await exports.DeleteAsync(exportId, part, cancellationToken);

        return await inbox.RunOnceAsync(connection, envelope.Id, null,
            async (transaction, token) =>
            {
                var tx = (NpgsqlTransaction)transaction;
                await using (var scope = new NpgsqlCommand(
                    "SELECT set_config('app.actor_sub',@actor,true)", connection, tx))
                {
                    scope.Parameters.AddWithValue("actor", machineActor.ToString("D"));
                    await scope.ExecuteNonQueryAsync(token);
                }
                foreach (var tenantId in tenantIds)
                {
                    await using (var tenantScope = new NpgsqlCommand(
                        "SELECT set_config('app.tenant_id',@tenant,true)", connection, tx))
                    {
                        tenantScope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
                        await tenantScope.ExecuteNonQueryAsync(token);
                    }
                    await using var member = new NpgsqlCommand("""
                        DELETE FROM tenant_member_profiles WHERE tenant_id=@tenant AND user_id=@user
                        """, connection, tx);
                    member.Parameters.AddWithValue("tenant", tenantId);
                    member.Parameters.AddWithValue("user", userId);
                    await member.ExecuteNonQueryAsync(token);
                }
                await using (var clearTenant = new NpgsqlCommand(
                    "SELECT set_config('app.tenant_id','',true)", connection, tx))
                    await clearTenant.ExecuteNonQueryAsync(token);
                await DeleteAsync(connection, tx, "avatar_uploads", userId, token);
                await DeleteAsync(connection, tx, "user_preferences", userId, token);
                await DeleteAsync(connection, tx, "data_export_jobs", userId, token);
                await using (var scrub = new NpgsqlCommand("""
                    UPDATE user_profiles SET display_name=NULL,time_zone='UTC',avatar_object_key=NULL,
                      mirror_first_name=NULL,mirror_last_name=NULL,mirror_locale=NULL,
                      updated_at=now(),deleted_at=COALESCE(deleted_at,now())
                    WHERE user_id=@user
                    """, connection, tx))
                {
                    scrub.Parameters.AddWithValue("user", userId);
                    if (await scrub.ExecuteNonQueryAsync(token) != 1)
                        throw new InvalidPlatformEventException();
                }
                var requestId = envelope.CorrelationId;
                await using (var tombstone = new NpgsqlCommand("""
                    INSERT INTO profile_user_tombstones(user_id,request_id) VALUES (@user,@request)
                    ON CONFLICT (user_id) DO NOTHING;
                    UPDATE profile_user_erasure_requests
                      SET status='pending_external_identity_deletion',updated_at=now()
                    WHERE request_id=@request AND user_id=@user;
                    """, connection, tx))
                {
                    tombstone.Parameters.AddWithValue("user", userId);
                    tombstone.Parameters.AddWithValue("request", requestId);
                    if (await tombstone.ExecuteNonQueryAsync(token) < 1)
                        throw new InvalidPlatformEventException();
                }
                await using (var audit = new NpgsqlCommand("""
                    INSERT INTO profile_audit_events
                      (id,tenant_id,actor_sub,target_sub,action,reason,correlation_id)
                    VALUES (@id,NULL,@actor,@target,'profile.user.erasure-marked',
                      'application-user-erasure',@correlation)
                    """, connection, tx))
                {
                    audit.Parameters.AddWithValue("id", Guid.NewGuid());
                    audit.Parameters.AddWithValue("actor", machineActor);
                    audit.Parameters.AddWithValue("target", userId);
                    audit.Parameters.AddWithValue("correlation", requestId);
                    await audit.ExecuteNonQueryAsync(token);
                }
                var confirmationId = Guid.NewGuid();
                var payload = JsonSerializer.Serialize(new
                {
                    specversion = "1.0",
                    id = confirmationId,
                    type = ProfileErasureConfirmedV1.EventType,
                    source = ProfileErasureConfirmedV1.EventSource,
                    time = DateTimeOffset.UtcNow,
                    correlationid = requestId,
                    actor = new { sub = machineActor.ToString("D"), type = "machine" },
                    data = new { scope = "user", userId, service = "profile" }
                });
                await using var outbox = new NpgsqlCommand("""
                    INSERT INTO outbox(id,type,payload) VALUES (@id,@type,@payload::jsonb)
                    """, connection, tx);
                outbox.Parameters.AddWithValue("id", confirmationId);
                outbox.Parameters.AddWithValue("type", ProfileErasureConfirmedV1.EventType);
                outbox.Parameters.AddWithValue("payload", payload);
                await outbox.ExecuteNonQueryAsync(token);
            }, cancellationToken);
    }

    private static async Task<(string[] AvatarKeys, Guid[] ExportIds)> ObjectIdsAsync(
        NpgsqlConnection connection, Guid userId, CancellationToken token)
    {
        var keys = new List<string>();
        await using (var avatars = new NpgsqlCommand(
            "SELECT object_key FROM avatar_uploads WHERE user_id=@user", connection))
        {
            avatars.Parameters.AddWithValue("user", userId);
            await using var reader = await avatars.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) keys.Add(reader.GetString(0));
        }
        var ids = new List<Guid>();
        await using (var exports = new NpgsqlCommand(
            "SELECT id FROM data_export_jobs WHERE user_id=@user", connection))
        {
            exports.Parameters.AddWithValue("user", userId);
            await using var reader = await exports.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetGuid(0));
        }
        return (keys.ToArray(), ids.ToArray());
    }

    private async Task<bool> RecordTenantConfirmationAsync(
        PlatformEvent<AccessErasureConfirmedV1> envelope, CancellationToken token)
    {
        var tenantId = envelope.TenantId!.Value;
        await using var connection = await database.Source.OpenConnectionAsync(token);
        return await inbox.RunOnceAsync(connection, envelope.Id, tenantId,
            async (transaction, cancellationToken) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT 1 FROM profile_tenant_tombstones WHERE tenant_id=@tenant
                    """, connection, (NpgsqlTransaction)transaction);
                command.Parameters.AddWithValue("tenant", tenantId);
                if (await command.ExecuteScalarAsync(cancellationToken) is null)
                    throw new InvalidPlatformEventException();
            }, token);
    }

    private static async Task<Guid[]> TenantIdsAsync(NpgsqlConnection connection, Guid userId,
        CancellationToken token)
    {
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var scope = new NpgsqlCommand("""
            SELECT set_config('app.export_user_id',@user,true),
                   set_config('app.export_mode','personal',true)
            """, connection, transaction))
        {
            scope.Parameters.AddWithValue("user", userId.ToString("D"));
            await scope.ExecuteNonQueryAsync(token);
        }
        var tenants = new List<Guid>();
        await using (var command = new NpgsqlCommand("""
            SELECT tenant_id FROM tenant_member_profiles WHERE user_id=@user ORDER BY tenant_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("user", userId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) tenants.Add(reader.GetGuid(0));
        }
        await transaction.CommitAsync(token);
        return tenants.ToArray();
    }

    private static async Task DeleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string table, Guid userId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            $"DELETE FROM {table} WHERE user_id=@user", connection, transaction);
        command.Parameters.AddWithValue("user", userId);
        await command.ExecuteNonQueryAsync(token);
    }
}
