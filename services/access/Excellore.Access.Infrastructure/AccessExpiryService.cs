using System.Text.Json;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Access.Infrastructure;

/// <summary>Expires one tenant's time-limited access in a single RLS-scoped transaction.</summary>
public sealed class AccessExpiryService(NpgsqlDataSource database, ITenantIdAccessor tenant)
{
    public async Task<int> ExpireAsync(Guid machineActorId, CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.TenantId;
        if (machineActorId == Guid.Empty)
            throw new ArgumentException("Machine actor is required.", nameof(machineActorId));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id',@tenant,true)", connection, transaction))
        {
            scope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }

        var grants = new List<(Guid Id, Guid StaffId, DateTimeOffset ExpiresAt, string PriorStatus)>();
        await using (var command = new NpgsqlCommand("""
            WITH selected AS (
              SELECT id,status FROM support_grants
              WHERE tenant_id=@tenant AND status IN ('requested','active') AND expires_at<=now()
              FOR UPDATE
            ), updated AS (
              UPDATE support_grants g SET status='expired' FROM selected s
              WHERE g.id=s.id
              RETURNING g.id,g.staff_user_id,g.expires_at,s.status AS prior_status
            ) SELECT id,staff_user_id,expires_at,prior_status FROM updated
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                grants.Add((reader.GetGuid(0), reader.GetGuid(1),
                    reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3)));
        }

        foreach (var grant in grants)
        {
            if (grant.PriorStatus == "active")
            {
                await ExecuteAsync(connection, transaction, """
                UPDATE tenant_members SET status='removed',updated_at=now()
                WHERE tenant_id=@tenant AND user_id=@staff AND is_support
                  AND support_grant_id=@grant AND status<>'removed'
                """, cancellationToken, ("tenant", tenantId), ("staff", grant.StaffId),
                ("grant", grant.Id));
                await WriteSupportGrantRevokedAsync(connection, transaction, tenantId, grant.Id,
                    grant.StaffId, grant.ExpiresAt, machineActorId, cancellationToken);
                await WriteMemberRemovedAsync(connection, transaction, tenantId,
                    grant.StaffId, machineActorId, cancellationToken);
            }
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, machineActorId,
                "service", "access.supportgrant.expired", "support-grant", grant.Id.ToString("D"),
                "Expiry reached", cancellationToken);
        }

        var assignments = new List<(Guid Id, Guid UserId, Guid RoleId, DateTimeOffset? ExpiresAt)>();
        await using (var command = new NpgsqlCommand("""
            UPDATE role_assignments SET revoked_at=now()
            WHERE tenant_id=@tenant AND revoked_at IS NULL
              AND (expires_at<=now() OR EXISTS (
                SELECT 1 FROM tenant_members m WHERE m.tenant_id=role_assignments.tenant_id
                  AND m.user_id=role_assignments.user_id AND m.is_support AND m.status='removed'))
            RETURNING id,user_id,role_id,expires_at
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                assignments.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        }
        foreach (var assignment in assignments)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, machineActorId,
                "service", "access.assignment.expired", "assignment", assignment.Id.ToString("D"),
                "Expiry reached", cancellationToken);
            await AccessAuthzChangeWriter.WriteAssignmentEventAsync(connection, transaction,
                tenantId, assignment.UserId, assignment.RoleId, assignment.ExpiresAt,
                machineActorId, "revoked", cancellationToken, "machine");
        }

        var expiredGroups = new List<Guid>();
        await using (var command = new NpgsqlCommand("""
            UPDATE group_role_assignments SET revoked_at=now()
            WHERE tenant_id=@tenant AND revoked_at IS NULL AND expires_at<=now()
            RETURNING id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) expiredGroups.Add(reader.GetGuid(0));
        }
        foreach (var assignmentId in expiredGroups)
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, machineActorId,
                "service", "access.group.assignment.expired", "group-assignment",
                assignmentId.ToString("D"), "Expiry reached", cancellationToken);

        var count = grants.Count + assignments.Count + expiredGroups.Count;
        if (grants.Any(g => g.PriorStatus == "active") || assignments.Count > 0 || expiredGroups.Count > 0)
            await AccessAuthzChangeWriter.BumpTenantAsync(connection, transaction, tenantId,
                machineActorId, cancellationToken, "machine");
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    private static async Task WriteSupportGrantRevokedAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, Guid grantId, Guid staffId,
        DateTimeOffset expiresAt, Guid actorId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        const string type = "access.supportgrant.revoked.v1";
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0", id, type, source = "excellore/access",
            time = DateTimeOffset.UtcNow, tenantid = tenantId, correlationid = Guid.NewGuid(),
            actor = new { sub = actorId.ToString("D"), type = "machine" },
            data = new { tenantId, grantId, staffUserId = staffId, expiresAt }
        });
        await ExecuteAsync(connection, transaction, """
            INSERT INTO outbox (id,type,tenant_id,payload)
            VALUES (@id,@type,@tenant,@payload::jsonb)
            """, cancellationToken, ("id", id), ("type", type), ("tenant", tenantId),
            ("payload", payload));
    }

    private static async Task WriteMemberRemovedAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, Guid userId, Guid actorId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        const string type = "tenant.member.removed.v1";
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0", id, type, source = "excellore/access",
            time = DateTimeOffset.UtcNow, tenantid = tenantId, correlationid = Guid.NewGuid(),
            actor = new { sub = actorId.ToString("D"), type = "machine" },
            data = new { tenantId, userId }
        });
        await ExecuteAsync(connection, transaction, """
            INSERT INTO outbox (id,type,tenant_id,payload)
            VALUES (@id,@type,@tenant,@payload::jsonb)
            """, cancellationToken, ("id", id), ("type", type), ("tenant", tenantId),
            ("payload", payload));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken token, params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(token);
    }
}
