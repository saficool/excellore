using System.Text.Json;
using System.Text.RegularExpressions;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record TenantSettingsView(Guid TenantId, string Name, string Slug, string Plan);
public sealed record ChangeTenantSettingsRequest(string? Name, string? Slug, string? Reason);
public enum TenantSettingsStatus { Changed, Invalid, Forbidden, StepUpRequired, NotFound, Conflict }
public sealed record TenantSettingsResult(TenantSettingsStatus Status, TenantSettingsView? Settings = null);

public sealed class TenantSettingsService(NpgsqlDataSource dataSource,
    ITenantContextAccessor tenantContext, ICurrentUser user, AccessResolutionService resolver)
{
    private static readonly Regex SlugPattern = new("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReservedSlugs =
        "www api auth admin platform t static assets support status help login logout app apps internal profile access shell excellore"
            .Split(' ').ToHashSet(StringComparer.Ordinal);

    public async Task<TenantSettingsView?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT t.name, t.slug, t.plan FROM tenants t
            JOIN tenant_settings s ON s.tenant_id = t.id
            WHERE t.id = @tenant AND t.status = 'active'
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TenantSettingsView(tenantContext.TenantId, reader.GetString(0),
                reader.GetString(1), reader.GetString(2)) : null;
    }

    public async Task<TenantSettingsResult> ChangeAsync(ChangeTenantSettingsRequest? request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId;
        var actorId = user.UserId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            guard.Parameters.AddWithValue("key", AccessAuditWriter.AuditLock);
            await guard.ExecuteNonQueryAsync(cancellationToken);
        }
        await SetTenantAsync(connection, transaction, cancellationToken);
        string? oldName = null;
        string? oldSlug = null;
        string? plan = null;
        await using (var command = new NpgsqlCommand("""
            SELECT name, slug, plan FROM tenants WHERE id = @tenant AND status = 'active' FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                oldName = reader.GetString(0);
                oldSlug = reader.GetString(1);
                plan = reader.GetString(2);
            }
        }
        if (oldName is null || oldSlug is null || plan is null)
            return await DenyAsync(TenantSettingsStatus.NotFound, "tenant-inactive");
        var current = await resolver.ResolveAsync(actorId, tenantId, cancellationToken);
        if (!current.Allowed)
            return await DenyAsync(TenantSettingsStatus.NotFound, "membership-inactive");
        if (!current.Permissions.Contains("access.settings.manage", StringComparer.Ordinal))
            return await DenyAsync(TenantSettingsStatus.Forbidden, "permission-missing");
        if (!user.HasRecentMfa)
            return await DenyAsync(TenantSettingsStatus.StepUpRequired, "step-up-required");

        var newName = request?.Name?.Trim() ?? oldName;
        var newSlug = request?.Slug ?? oldSlug;
        var reason = request?.Reason?.Trim();
        if (request is null || request.Name is null && request.Slug is null ||
            newName.Length is < 1 or > 160 ||
            newSlug.Length is < 3 or > 40 || !SlugPattern.IsMatch(newSlug) ||
            ReservedSlugs.Contains(newSlug) || !AccessRoleGrantPolicy.ValidReasonCode(reason))
            return await DenyAsync(TenantSettingsStatus.Invalid, "invalid-settings");

        var changed = new List<string>();
        if (newName != oldName) changed.Add("name");
        if (newSlug != oldSlug) changed.Add("slug");
        if (changed.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(TenantSettingsStatus.Changed,
                new TenantSettingsView(tenantId, oldName, oldSlug, plan));
        }
        await transaction.SaveAsync("metadata_update", cancellationToken);
        try
        {
            await using var update = new NpgsqlCommand("""
                UPDATE tenants SET name = @name, slug = @slug, updated_at = now()
                WHERE id = @tenant
                """, connection, transaction);
            update.Parameters.AddWithValue("name", newName);
            update.Parameters.AddWithValue("slug", newSlug);
            update.Parameters.AddWithValue("tenant", tenantId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation &&
            exception.ConstraintName == "tenants_slug_key")
        {
            await transaction.RollbackAsync("metadata_update", cancellationToken);
            return await DenyAsync(TenantSettingsStatus.Conflict, "slug-unavailable");
        }
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
            "human", "access.settings.changed", "tenant", tenantId.ToString("D"),
            reason!, cancellationToken,
            beforeJson: JsonSerializer.Serialize(new { changed }),
            afterJson: JsonSerializer.Serialize(new { changed }), acr: user.Acr);
        var eventId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0", id = eventId, type = "tenant.updated.v1",
            source = "excellore/access", time = DateTimeOffset.UtcNow,
            tenantid = tenantId, correlationid = Guid.NewGuid(),
            actor = new { sub = actorId.ToString("D"), type = "human" },
            data = new { tenantId, changed }
        });
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO outbox (id, type, tenant_id, payload)
            VALUES (@id, 'tenant.updated.v1', @tenant, @payload::jsonb)
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("id", eventId);
            outbox.Parameters.AddWithValue("tenant", tenantId);
            outbox.Parameters.AddWithValue("payload", payload);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(TenantSettingsStatus.Changed,
            new TenantSettingsView(tenantId, newName, newSlug, plan));

        async Task<TenantSettingsResult> DenyAsync(TenantSettingsStatus status, string code)
        {
            await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorId,
                "human", "access.settings.change.denied", "tenant", tenantId.ToString("D"),
                code, cancellationToken, acr: user.Acr);
            await transaction.CommitAsync(cancellationToken);
            return new(status);
        }
    }

    private async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true)",
            connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
