using System.Text.Json;
using Excellore.Contracts;
using Npgsql;

namespace Excellore.Access.Infrastructure;

/// <summary>Erases live Access references while retaining unchanged restricted audit originals.</summary>
public sealed class AccessUserErasureProjector(NpgsqlDataSource database, Guid machineActor)
{
    public async Task<bool> ApplyAsync(ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var envelope = ProfileDeletionRequestedV1.Parse(body.Span);
        var userId = envelope.Data!.UserId;
        if (machineActor == Guid.Empty)
            throw new InvalidOperationException("A machine identity UUID is required.");

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtextextended(@user::text,0))", connection))
        {
            acquire.Parameters.AddWithValue("user", userId);
            await acquire.ExecuteNonQueryAsync(cancellationToken);
        }
        try
        {
            if (await ReceivedAsync(connection, envelope.Id, cancellationToken)) return false;

            var tombstoneId = await DisableIdentityAsync(connection, envelope.Id, userId,
                cancellationToken);
            var tenants = await TenantIdsAsync(connection, userId, cancellationToken);
            foreach (var tenantId in tenants)
                await EraseTenantAsync(connection, tenantId, userId, tombstoneId, machineActor,
                    cancellationToken);
            await ConfirmAsync(connection, envelope, userId, machineActor, cancellationToken);
            return true;
        }
        finally
        {
            await using var release = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended(@user::text,0))", connection);
            release.Parameters.AddWithValue("user", userId);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task<Guid> DisableIdentityAsync(NpgsqlConnection connection,
        Guid requestId, Guid userId, CancellationToken token)
    {
        await using var transaction = await connection.BeginTransactionAsync(token);
        var tombstoneId = Guid.CreateVersion7();
        await using (var request = new NpgsqlCommand("""
            INSERT INTO user_erasure_requests(request_id,user_id,tombstone_id)
            VALUES (@request,@user,@tombstone)
            ON CONFLICT (user_id) DO NOTHING
            """, connection, transaction))
        {
            request.Parameters.AddWithValue("request", requestId);
            request.Parameters.AddWithValue("user", userId);
            request.Parameters.AddWithValue("tombstone", tombstoneId);
            await request.ExecuteNonQueryAsync(token);
        }
        await using (var existing = new NpgsqlCommand(
            "SELECT tombstone_id FROM user_erasure_requests WHERE user_id=@user",
            connection, transaction))
        {
            existing.Parameters.AddWithValue("user", userId);
            tombstoneId = (Guid)(await existing.ExecuteScalarAsync(token)
                ?? throw new InvalidPlatformEventException());
        }
        await using (var identity = new NpgsqlCommand("""
            UPDATE identities SET email=@email,email_verified=false,given_name=NULL,
              family_name=NULL,status='deleted',updated_at=now()
            WHERE user_id=@user
            """, connection, transaction))
        {
            identity.Parameters.AddWithValue("email", $"erased-{tombstoneId:N}@invalid.excellore");
            identity.Parameters.AddWithValue("user", userId);
            if (await identity.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidPlatformEventException();
        }
        await transaction.CommitAsync(token);
        return tombstoneId;
    }

    private static async Task<Guid[]> TenantIdsAsync(NpgsqlConnection connection, Guid userId,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            "SELECT tenant_id FROM user_tenant_index WHERE user_id=@user ORDER BY tenant_id",
            connection);
        command.Parameters.AddWithValue("user", userId);
        var result = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(reader.GetGuid(0));
        return result.ToArray();
    }

    private static async Task EraseTenantAsync(NpgsqlConnection connection, Guid tenantId,
        Guid userId, Guid tombstoneId, Guid machineActor, CancellationToken token)
    {
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction))
        {
            scope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync(token);
        }
        await ExecuteAsync(connection, transaction,
            "DELETE FROM ownership_transfers WHERE tenant_id=@tenant AND (from_user_id=@user OR to_user_id=@user)",
            tenantId, userId, tombstoneId, token);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM group_members WHERE tenant_id=@tenant AND user_id=@user",
            tenantId, userId, tombstoneId, token);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM role_assignments WHERE tenant_id=@tenant AND user_id=@user",
            tenantId, userId, tombstoneId, token);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM support_grants WHERE tenant_id=@tenant AND staff_user_id=@user",
            tenantId, userId, tombstoneId, token);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM role_assignment_batches WHERE tenant_id=@tenant AND @user=ANY(target_user_ids)",
            tenantId, userId, tombstoneId, token);

        foreach (var sql in new[]
        {
            "UPDATE roles SET created_by=@tombstone WHERE tenant_id=@tenant AND created_by=@user",
            "UPDATE role_assignments SET granted_by=@tombstone WHERE tenant_id=@tenant AND granted_by=@user",
            "UPDATE group_role_assignments SET granted_by=@tombstone WHERE tenant_id=@tenant AND granted_by=@user",
            "UPDATE tenant_members SET invited_by=@tombstone WHERE tenant_id=@tenant AND invited_by=@user AND user_id<>@user",
            "UPDATE invitations SET invited_by=CASE WHEN invited_by=@user THEN @tombstone ELSE invited_by END, accepted_by=CASE WHEN accepted_by=@user THEN @tombstone ELSE accepted_by END WHERE tenant_id=@tenant AND (invited_by=@user OR accepted_by=@user)",
            "UPDATE access_requests SET requester_id=CASE WHEN requester_id=@user THEN @tombstone ELSE requester_id END, decided_by=CASE WHEN decided_by=@user THEN @tombstone ELSE decided_by END WHERE tenant_id=@tenant AND (requester_id=@user OR decided_by=@user)",
            "UPDATE support_grants SET approved_by=@tombstone WHERE tenant_id=@tenant AND approved_by=@user",
            "UPDATE audit_exports SET requested_by=@tombstone WHERE tenant_id=@tenant AND requested_by=@user",
            "UPDATE tenant_exports SET requested_by=@tombstone WHERE tenant_id=@tenant AND requested_by=@user",
            "UPDATE role_assignment_batches SET requested_by=@tombstone WHERE tenant_id=@tenant AND requested_by=@user"
        })
            await ExecuteAsync(connection, transaction, sql, tenantId, userId, tombstoneId, token);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM tenant_members WHERE tenant_id=@tenant AND user_id=@user",
            tenantId, userId, tombstoneId, token);
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, machineActor,
            "service", "access.user.erasure-marked", "user-tombstone", tombstoneId.ToString("D"),
            "application-user-erasure", token);
        await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction, tenantId,
            machineActor, token);
        await transaction.CommitAsync(token);
    }

    private static async Task ConfirmAsync(NpgsqlConnection connection,
        PlatformEvent<ProfileDeletionRequestedV1> envelope, Guid userId, Guid machineActor,
        CancellationToken token)
    {
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var index = new NpgsqlCommand(
            "DELETE FROM user_tenant_index WHERE user_id=@user", connection, transaction))
        {
            index.Parameters.AddWithValue("user", userId);
            await index.ExecuteNonQueryAsync(token);
        }
        var eventId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0",
            id = eventId,
            type = AccessErasureConfirmedV1.EventType,
            source = AccessErasureConfirmedV1.EventSource,
            time = DateTimeOffset.UtcNow,
            correlationid = envelope.CorrelationId,
            actor = new { sub = machineActor.ToString("D"), type = "machine" },
            data = new { scope = "user", userId, service = "access" }
        });
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO outbox(id,type,tenant_id,payload) VALUES (@id,@type,NULL,@payload::jsonb)
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("id", eventId);
            outbox.Parameters.AddWithValue("type", AccessErasureConfirmedV1.EventType);
            outbox.Parameters.AddWithValue("payload", payload);
            await outbox.ExecuteNonQueryAsync(token);
        }
        await using (var inbox = new NpgsqlCommand(
            "INSERT INTO inbox(event_id) VALUES (@id)", connection, transaction))
        {
            inbox.Parameters.AddWithValue("id", envelope.Id);
            await inbox.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    private static async Task<bool> ReceivedAsync(NpgsqlConnection connection, Guid eventId,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM inbox WHERE event_id=@id", connection);
        command.Parameters.AddWithValue("id", eventId);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, Guid tenantId, Guid userId, Guid tombstoneId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("tombstone", tombstoneId);
        await command.ExecuteNonQueryAsync(token);
    }
}
