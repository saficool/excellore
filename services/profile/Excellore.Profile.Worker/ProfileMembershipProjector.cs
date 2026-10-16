using Excellore.Contracts;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Profile.Worker;

public sealed class ProfileWorkerDatabase(NpgsqlDataSource source)
{
    public NpgsqlDataSource Source { get; } = source;
}

/// <summary>Projects Access membership events atomically with inbox receipt under tenant RLS.</summary>
public sealed class ProfileMembershipProjector(ProfileWorkerDatabase database, PostgresEventInbox inbox)
{
    public async Task<bool> ApplyAsync(string eventType, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        Guid eventId;
        Guid tenantId;
        Guid userId;
        string action;
        switch (eventType)
        {
            case TenantMemberAddedV1.EventType:
                var added = TenantMemberAddedV1.Parse(body.Span);
                eventId = added.Id;
                tenantId = added.TenantId!.Value;
                userId = added.Data!.UserId;
                action = "active";
                break;
            case TenantMemberSuspendedV1.EventType:
                var suspended = TenantMemberSuspendedV1.Parse(body.Span);
                eventId = suspended.Id;
                tenantId = suspended.TenantId!.Value;
                userId = suspended.Data!.UserId;
                action = "suspended";
                break;
            case TenantMemberReactivatedV1.EventType:
                var reactivated = TenantMemberReactivatedV1.Parse(body.Span);
                eventId = reactivated.Id;
                tenantId = reactivated.TenantId!.Value;
                userId = reactivated.Data!.UserId;
                action = "active";
                break;
            case TenantMemberRemovedV1.EventType:
                var removed = TenantMemberRemovedV1.Parse(body.Span);
                eventId = removed.Id;
                tenantId = removed.TenantId!.Value;
                userId = removed.Data!.UserId;
                action = "removed";
                break;
            default:
                throw new InvalidPlatformEventException();
        }

        await using var connection = await database.Source.OpenConnectionAsync(cancellationToken);
        return await inbox.RunOnceAsync(connection, eventId, tenantId, async (transaction, token) =>
        {
            await using (var serial = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@tenant::text,0))",
                connection, (NpgsqlTransaction)transaction))
            {
                serial.Parameters.AddWithValue("tenant", tenantId);
                await serial.ExecuteNonQueryAsync(token);
            }
            await using (var tombstone = new NpgsqlCommand(
                "SELECT 1 FROM profile_tenant_tombstones WHERE tenant_id=@tenant",
                connection, (NpgsqlTransaction)transaction))
            {
                tombstone.Parameters.AddWithValue("tenant", tenantId);
                if (await tombstone.ExecuteScalarAsync(token) is not null)
                    return;
            }
            var sql = action switch
            {
                "removed" => "DELETE FROM tenant_member_profiles WHERE tenant_id=@tenant AND user_id=@user",
                "suspended" => "UPDATE tenant_member_profiles SET status='suspended',updated_at=now() WHERE tenant_id=@tenant AND user_id=@user",
                _ => """
                    INSERT INTO tenant_member_profiles (tenant_id,user_id,status)
                    VALUES (@tenant,@user,'active')
                    ON CONFLICT (tenant_id,user_id) DO UPDATE SET status='active',updated_at=now()
                    """
            };
            await using var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction)transaction);
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("user", userId);
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }
}
