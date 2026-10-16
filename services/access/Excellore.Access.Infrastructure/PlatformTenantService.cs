using System.Text.Json;
using System.Text.RegularExpressions;
using Excellore.Auth;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record PlatformTenantView(Guid Id, string Slug, string Name, string Status,
    string Plan, string Region, long AuthzVersion, DateTimeOffset CreatedAt,
    DateTimeOffset? DeletionDueAt);
public sealed record PlatformTenantDetail(PlatformTenantView Tenant,
    IReadOnlyList<PlatformTenantAppView> Apps);
public sealed record PlatformTenantAppView(string Id, string Name, bool Core,
    string Status, JsonElement Features);
public sealed record UpdatePlatformTenantRequest(string? Name, string? Slug, string? Plan,
    string? Reason);
public sealed record PlatformTenantActionRequest(string? Reason);
public sealed record SetPlatformTenantAppRequest(bool Enabled,
    Dictionary<string, int>? Features, string? Reason);
public enum PlatformTenantStatus { Updated, Unchanged, Invalid, StepUpRequired, NotFound, Conflict }
public sealed record PlatformTenantResult(PlatformTenantStatus Status,
    PlatformTenantDetail? Tenant = null, DateTimeOffset? DueAt = null);

/// <summary>Platform metadata and explicitly scoped app-enable operations.</summary>
public sealed class PlatformTenantService(NpgsqlDataSource database, ICurrentUser user)
{
    private static readonly Regex SlugPattern = new("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$",
        RegexOptions.CultureInvariant);
    private static readonly Regex PlanPattern = new("^[a-z][a-z0-9-]{0,39}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex FeaturePattern = new("^[a-z][a-zA-Z0-9]{0,39}$",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReservedSlugs =
        "www api auth admin platform t static assets support status help login logout app apps internal profile access shell excellore"
            .Split(' ').ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<PlatformTenantView>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id,slug,name,status,plan,region,authz_version,created_at,deletion_due_at
            FROM tenants ORDER BY slug LIMIT 1000
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<PlatformTenantView>();
        while (await reader.ReadAsync(cancellationToken)) tenants.Add(ReadTenant(reader));
        return tenants;
    }

    public async Task<PlatformTenantDetail?> GetAsync(Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty) return null;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await ScopeAsync(connection, tx, tenantId, cancellationToken);
        var tenant = await ReadTenantAsync(connection, tx, tenantId, cancellationToken);
        if (tenant is null) return null;
        var apps = await ReadAppsAsync(connection, tx, tenantId, cancellationToken);
        return new(tenant, apps);
    }

    public async Task<PlatformTenantResult> UpdateAsync(Guid tenantId,
        UpdatePlatformTenantRequest? request, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || request is null ||
            !AccessRoleGrantPolicy.ValidReasonCode(request.Reason?.Trim()) ||
            request.Name is null && request.Slug is null && request.Plan is null ||
            request.Name is not null && request.Name.Trim() is not { Length: > 0 and <= 160 } ||
            request.Slug is not null && !ValidSlug(request.Slug) ||
            request.Plan is not null && !ValidPlan(request.Plan))
            return new(PlatformTenantStatus.Invalid);
        if (!user.HasRecentMfa) return new(PlatformTenantStatus.StepUpRequired);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await LockAndScopeAsync(connection, tx, tenantId, cancellationToken);
        var before = await ReadTenantAsync(connection, tx, tenantId, cancellationToken, true);
        if (before is null) return new(PlatformTenantStatus.NotFound);
        if (before.Status is "pending_deletion" or "deleted")
            return new(PlatformTenantStatus.Conflict);
        var name = request.Name?.Trim() ?? before.Name;
        var slug = request.Slug ?? before.Slug;
        var plan = request.Plan ?? before.Plan;
        var changed = new List<string>();
        if (name != before.Name) changed.Add("name");
        if (slug != before.Slug) changed.Add("slug");
        if (plan != before.Plan) changed.Add("plan");
        if (changed.Count == 0) return new(PlatformTenantStatus.Unchanged);
        try
        {
            await ExecuteAsync(connection, tx, """
                UPDATE tenants SET name=@name,slug=@slug,plan=@plan,updated_at=now()
                WHERE id=@tenant
                """, cancellationToken, ("name", name), ("slug", slug),
                ("plan", plan), ("tenant", tenantId));
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation &&
                                             error.ConstraintName == "tenants_slug_key")
        {
            return new(PlatformTenantStatus.Conflict);
        }
        await AuditAsync(connection, tx, tenantId, "access.platform.tenant.updated",
            request.Reason!.Trim(), cancellationToken,
            JsonSerializer.Serialize(new { before.Name, before.Slug, before.Plan }),
            JsonSerializer.Serialize(new { name, slug, plan }));
        await AccessAuthzChangeWriter.BumpTenantAsync(connection, tx,
            tenantId, user.UserId, cancellationToken);
        await EventAsync(connection, tx, tenantId, "tenant.updated.v1",
            new { tenantId, changed }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(PlatformTenantStatus.Updated);
    }

    public Task<PlatformTenantResult> SuspendAsync(Guid tenantId,
        PlatformTenantActionRequest? request, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(tenantId, request, "suspended", cancellationToken);

    public Task<PlatformTenantResult> ReactivateAsync(Guid tenantId,
        PlatformTenantActionRequest? request, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(tenantId, request, "active", cancellationToken);

    private async Task<PlatformTenantResult> ChangeStatusAsync(Guid tenantId,
        PlatformTenantActionRequest? request, string next, CancellationToken cancellationToken)
    {
        var reason = request?.Reason?.Trim();
        if (tenantId == Guid.Empty || !AccessRoleGrantPolicy.ValidReasonCode(reason))
            return new(PlatformTenantStatus.Invalid);
        if (!user.HasRecentMfa) return new(PlatformTenantStatus.StepUpRequired);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await LockAndScopeAsync(connection, tx, tenantId, cancellationToken);
        var before = await ReadTenantAsync(connection, tx, tenantId, cancellationToken, true);
        if (before is null) return new(PlatformTenantStatus.NotFound);
        if (before.Status == next) return new(PlatformTenantStatus.Unchanged);
        if (next == "suspended" && before.Status != "active" ||
            next == "active" && before.Status != "suspended")
            return new(PlatformTenantStatus.Conflict);
        await ExecuteAsync(connection, tx, """
            UPDATE tenants SET status=@status,updated_at=now() WHERE id=@tenant
            """, cancellationToken, ("status", next), ("tenant", tenantId));
        await AuditAsync(connection, tx, tenantId,
            next == "suspended" ? "access.platform.tenant.suspended" :
                "access.platform.tenant.reactivated", reason!, cancellationToken,
            JsonSerializer.Serialize(new { status = before.Status }),
            JsonSerializer.Serialize(new { status = next }));
        await AccessAuthzChangeWriter.BumpTenantAsync(connection, tx,
            tenantId, user.UserId, cancellationToken);
        await EventAsync(connection, tx, tenantId,
            next == "suspended" ? "tenant.suspended.v1" : "tenant.reactivated.v1",
            new { tenantId, reasonCode = reason }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(PlatformTenantStatus.Updated);
    }

    public async Task<PlatformTenantResult> SetAppAsync(Guid tenantId, string appId,
        SetPlatformTenantAppRequest? request, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || !TenantEventCode(appId) || request is null ||
            !AccessRoleGrantPolicy.ValidReasonCode(request.Reason?.Trim()) ||
            request.Features is { Count: > 20 } ||
            request.Features?.Any(entry => !FeaturePattern.IsMatch(entry.Key) ||
                entry.Value is < 0 or > 1_000_000) is true)
            return new(PlatformTenantStatus.Invalid);
        if (!user.HasRecentMfa) return new(PlatformTenantStatus.StepUpRequired);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await LockAndScopeAsync(connection, tx, tenantId, cancellationToken);
        var tenant = await ReadTenantAsync(connection, tx, tenantId, cancellationToken, true);
        if (tenant is null) return new(PlatformTenantStatus.NotFound);
        if (tenant.Status is "pending_deletion" or "deleted")
            return new(PlatformTenantStatus.Conflict);
        bool core;
        await using (var app = Command(connection, tx,
            "SELECT core FROM apps WHERE id=@app", ("app", appId)))
        {
            if (await app.ExecuteScalarAsync(cancellationToken) is not bool value)
                return new(PlatformTenantStatus.NotFound);
            core = value;
        }
        if (core && !request.Enabled) return new(PlatformTenantStatus.Conflict);
        var status = request.Enabled ? "enabled" : "disabled";
        var features = request.Features is null ? null : JsonSerializer.Serialize(request.Features);
        var previousStatus = "disabled";
        string? previousFeatures = null;
        await using (var current = Command(connection, tx, """
            SELECT status,features::text FROM tenant_apps
            WHERE tenant_id=@tenant AND app_id=@app FOR UPDATE
            """, ("tenant", tenantId), ("app", appId)))
        await using (var reader = await current.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken))
            {
                previousStatus = reader.GetString(0);
                previousFeatures = reader.GetString(1);
            }
        if (previousStatus == status &&
            (features is null || JsonEquals(previousFeatures, features)))
            return new(PlatformTenantStatus.Unchanged);
        await ExecuteAsync(connection, tx, """
            INSERT INTO tenant_apps(tenant_id,app_id,status,features)
            VALUES (@tenant,@app,@status,COALESCE(@features::jsonb,'{}'::jsonb))
            ON CONFLICT(tenant_id,app_id) DO UPDATE SET status=EXCLUDED.status,
              features=CASE WHEN @features IS NULL THEN tenant_apps.features ELSE EXCLUDED.features END,
              updated_at=now()
            """, cancellationToken, ("tenant", tenantId), ("app", appId),
            ("status", status), ("features", (object?)features ?? DBNull.Value));
        await AuditAsync(connection, tx, tenantId, "access.platform.app.updated",
            request.Reason!.Trim(), cancellationToken,
            JsonSerializer.Serialize(new { appId, status = previousStatus }),
            JsonSerializer.Serialize(new { appId, status }));
        await AccessAuthzChangeWriter.BumpTenantAsync(connection, tx,
            tenantId, user.UserId, cancellationToken);
        if (previousStatus != status)
            await EventAsync(connection, tx, tenantId,
                request.Enabled ? "tenant.app.enabled.v1" : "tenant.app.disabled.v1",
                new { tenantId, appId }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(PlatformTenantStatus.Updated);
    }

    private static bool JsonEquals(string? left, string right)
    {
        if (left is null) return false;
        using var before = JsonDocument.Parse(left);
        using var after = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(before.RootElement, after.RootElement);
    }

    public async Task<PlatformTenantResult> RequestDeletionAsync(Guid tenantId,
        PlatformTenantActionRequest? request, CancellationToken cancellationToken = default)
    {
        var reason = request?.Reason?.Trim();
        if (tenantId == Guid.Empty || !AccessRoleGrantPolicy.ValidReasonCode(reason))
            return new(PlatformTenantStatus.Invalid);
        if (!user.HasRecentMfa) return new(PlatformTenantStatus.StepUpRequired);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await LockAndScopeAsync(connection, tx, tenantId, cancellationToken);
        var before = await ReadTenantAsync(connection, tx, tenantId, cancellationToken, true);
        if (before is null) return new(PlatformTenantStatus.NotFound);
        if (before.Status is not ("active" or "suspended"))
            return new(PlatformTenantStatus.Conflict);
        DateTimeOffset dueAt;
        await using (var command = Command(connection, tx, """
            UPDATE tenants SET status='pending_deletion',deletion_requested_at=now(),
              deletion_due_at=now()+interval '14 days',updated_at=now()
            WHERE id=@tenant RETURNING deletion_due_at
            """, ("tenant", tenantId)))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new(PlatformTenantStatus.NotFound);
            dueAt = new DateTimeOffset(reader.GetDateTime(0));
        }
        await AuditAsync(connection, tx, tenantId,
            "access.platform.tenant.deletion-requested", reason!, cancellationToken,
            JsonSerializer.Serialize(new { status = before.Status }),
            JsonSerializer.Serialize(new { status = "pending_deletion", dueAt }));
        await AccessAuthzChangeWriter.BumpTenantAsync(connection, tx,
            tenantId, user.UserId, cancellationToken);
        await EventAsync(connection, tx, tenantId,
            "tenant.deletion.requested.v1", new { tenantId, dueAt }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(PlatformTenantStatus.Updated, DueAt: dueAt);
    }

    private static bool TenantEventCode(string? value) =>
        value is { Length: >= 1 and <= 64 } && value[0] is >= 'a' and <= 'z' &&
        value.Skip(1).All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'
            or '_' or '-');

    private static bool ValidSlug(string slug) => SlugPattern.IsMatch(slug) &&
        !ReservedSlugs.Contains(slug);

    private static bool ValidPlan(string plan) => PlanPattern.IsMatch(plan);

    private async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid tenantId, string action, string reason, CancellationToken token,
        string? before = null, string? after = null)
    {
        await AccessAuditWriter.WriteAsync(connection, tx, tenantId, user.UserId,
            "human", action, "tenant", tenantId.ToString("D"), reason, token,
            beforeJson: before, afterJson: after, acr: user.Acr);
        await AccessAuditWriter.WriteAsync(connection, tx, null, user.UserId,
            "human", action, "tenant", tenantId.ToString("D"), reason, token,
            beforeJson: before, afterJson: after, acr: user.Acr);
    }

    private async Task EventAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid tenantId, string type, object data, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            specversion = "1.0", id, type, source = "excellore/access",
            time = DateTimeOffset.UtcNow, tenantid = tenantId,
            correlationid = Guid.NewGuid(),
            actor = new { sub = user.UserId.ToString("D"), type = "human" }, data
        });
        await ExecuteAsync(connection, tx, """
            INSERT INTO outbox(id,type,tenant_id,payload)
            VALUES (@id,@type,@tenant,@payload::jsonb)
            """, token, ("id", id), ("type", type), ("tenant", tenantId),
            ("payload", payload));
    }

    private static async Task LockAndScopeAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid tenantId, CancellationToken token)
    {
        await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(@key)", token,
            ("key", AccessAuditWriter.AuditLock));
        await ScopeAsync(connection, tx, tenantId, token);
    }

    private static async Task ScopeAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid tenantId, CancellationToken token)
    {
        await ExecuteAsync(connection, tx,
            "SELECT set_config('app.scope','platform',true)", token);
        await ExecuteAsync(connection, tx,
            "SELECT set_config('app.tenant_id',@tenant,true)", token,
            ("tenant", tenantId.ToString("D")));
    }

    private static async Task<PlatformTenantView?> ReadTenantAsync(NpgsqlConnection connection,
        NpgsqlTransaction tx, Guid tenantId, CancellationToken token, bool forUpdate = false)
    {
        var sql = """
            SELECT id,slug,name,status,plan,region,authz_version,created_at,deletion_due_at
            FROM tenants WHERE id=@tenant
            """ + (forUpdate ? " FOR UPDATE" : "");
        await using var command = Command(connection, tx, sql, ("tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadTenant(reader) : null;
    }

    private static PlatformTenantView ReadTenant(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetInt64(6), reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));

    private static async Task<IReadOnlyList<PlatformTenantAppView>> ReadAppsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid tenantId, CancellationToken token)
    {
        await using var command = Command(connection, tx, """
            SELECT a.id,a.name,a.core,COALESCE(ta.status,'disabled'),
              COALESCE(ta.features,'{}'::jsonb)::text
            FROM apps a LEFT JOIN tenant_apps ta ON ta.app_id=a.id AND ta.tenant_id=@tenant
            ORDER BY a.id
            """, ("tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(token);
        var apps = new List<PlatformTenantAppView>();
        while (await reader.ReadAsync(token))
        {
            using var features = JsonDocument.Parse(reader.GetString(4));
            apps.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2),
                reader.GetString(3), features.RootElement.Clone()));
        }
        return apps;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction tx,
        string sql, params (string Name, object Value)[] values)
    {
        var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string sql, CancellationToken token, params (string Name, object Value)[] values)
    {
        await using var command = Command(connection, tx, sql, values);
        await command.ExecuteNonQueryAsync(token);
    }
}
