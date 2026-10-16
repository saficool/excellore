using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace Excellore.Access.Infrastructure;

internal static class AccessAuthzChangeWriter
{
    internal static async Task<long> BumpTenantAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, Guid actorSub,
        CancellationToken cancellationToken, string actorType = "human")
    {
        await using var command = new NpgsqlCommand("""
            UPDATE tenants SET authz_version = authz_version + 1, updated_at = now()
            WHERE id = @tenant RETURNING authz_version
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        var version = (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Tenant unavailable for authorization update."));
        await WriteEventAsync(connection, transaction, tenantId, version, actorSub,
            actorType, Guid.NewGuid(), cancellationToken);
        return version;
    }

    internal static async Task WriteEventAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, long version, Guid actorSub,
        string actorType, Guid correlationId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0", id, type = "access.authz.changed.v1", source = "excellore/access",
            time = DateTimeOffset.UtcNow, tenantid = tenantId, correlationid = correlationId,
            actor = new { sub = actorSub.ToString("D"), type = actorType },
            data = new { tenantId, authzVersion = version }
        });
        await using var command = new NpgsqlCommand("""
            INSERT INTO outbox (id, type, tenant_id, payload)
            VALUES (@id, 'access.authz.changed.v1', @tenant, @payload::jsonb)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task WriteRoleEventAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, Guid roleId, Guid actorSub,
        string verb, CancellationToken cancellationToken)
    {
        if (verb is not ("created" or "updated" or "deleted"))
            throw new ArgumentOutOfRangeException(nameof(verb));
        var id = Guid.NewGuid();
        var type = $"access.role.{verb}.v1";
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0", id, type, source = "excellore/access",
            time = DateTimeOffset.UtcNow, tenantid = tenantId, correlationid = Guid.NewGuid(),
            actor = new { sub = actorSub.ToString("D"), type = "human" },
            data = new { tenantId, roleId }
        });
        await using var command = new NpgsqlCommand("""
            INSERT INTO outbox (id, type, tenant_id, payload)
            VALUES (@id, @type, @tenant, @payload::jsonb)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task WriteAssignmentEventAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenantId, Guid userId, Guid roleId,
        DateTimeOffset? expiresAt, Guid actorSub, string verb,
        CancellationToken cancellationToken, string actorType = "human")
    {
        if (verb is not ("granted" or "revoked"))
            throw new ArgumentOutOfRangeException(nameof(verb));
        var id = Guid.NewGuid();
        var type = $"access.assignment.{verb}.v1";
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0", id, type, source = "excellore/access",
            time = DateTimeOffset.UtcNow, tenantid = tenantId, correlationid = Guid.NewGuid(),
            actor = new { sub = actorSub.ToString("D"), type = actorType },
            data = new { tenantId, userId, roleId, expiresAt }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        await using var command = new NpgsqlCommand("""
            INSERT INTO outbox (id, type, tenant_id, payload)
            VALUES (@id, @type, @tenant, @payload::jsonb)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
