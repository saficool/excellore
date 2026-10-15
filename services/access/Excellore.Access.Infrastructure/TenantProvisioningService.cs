using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record CreateTenantRequest(string? Name, string? Slug, string? Plan,
    string? OwnerEmail, string[]? InitialApps);

public sealed record CreatedTenant(Guid TenantId, string Status, Guid OwnerInvitationId);

public enum CreateTenantStatus { Created, Invalid, SlugTaken, CatalogUnavailable }

public sealed record CreateTenantResult(CreateTenantStatus Status, CreatedTenant? Tenant = null);

public sealed class TenantProvisioningService(NpgsqlDataSource dataSource,
    InvitationDeliveryCoordinator? deliveryCoordinator = null)
{
    private static readonly Regex SlugPattern = new("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$", RegexOptions.CultureInvariant);
    private static readonly Regex PlanPattern = new("^[a-z][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReservedSlugs =
        "www api auth admin platform t static assets support status help login logout app apps internal profile access shell excellore"
            .Split(' ').ToHashSet(StringComparer.Ordinal);

    public async Task<CreateTenantResult> CreateAsync(CreateTenantRequest? request, Guid actorSub,
        CancellationToken cancellationToken = default, string? actorAcr = null)
    {
        if (request is null || !Valid(request)) return new(CreateTenantStatus.Invalid);
        var name = request.Name!.Trim();
        var slug = request.Slug!;
        var plan = request.Plan!;
        var email = request.OwnerEmail!.Trim();
        var requestedApps = request.InitialApps ?? [];

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "SELECT set_config('app.scope', 'platform', true)", cancellationToken);

        Guid ownerRole;
        await using (var command = Command(connection, transaction,
            "SELECT id FROM roles WHERE tenant_id IS NULL AND is_system AND name = 'owner'"))
        {
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is not Guid id) return new(CreateTenantStatus.CatalogUnavailable);
            ownerRole = id;
        }

        var apps = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Command(connection, transaction, "SELECT id, core FROM apps"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetBoolean(1)) apps.Add(reader.GetString(0));
        }
        if (!apps.Contains("access") || !apps.Contains("profile"))
            return new(CreateTenantStatus.CatalogUnavailable);
        foreach (var app in requestedApps)
        {
            await using var command = Command(connection, transaction,
                "SELECT count(*) FROM apps WHERE id = @id", ("id", app));
            if ((long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) != 1)
                return new(CreateTenantStatus.Invalid);
            apps.Add(app);
        }

        var tenantId = Guid.CreateVersion7();
        var invitationId = Guid.CreateVersion7();
        try
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO tenants (id, name, slug, plan, status)
                VALUES (@id, @name, @slug, @plan, 'provisioning')
                """, cancellationToken, ("id", tenantId), ("name", name), ("slug", slug), ("plan", plan));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation &&
            exception.ConstraintName == "tenants_slug_key")
        {
            return new(CreateTenantStatus.SlugTaken);
        }

        await ExecuteAsync(connection, transaction,
            "SELECT set_config('app.tenant_id', @tenant, true)", cancellationToken,
            ("tenant", tenantId.ToString("D")));
        foreach (var app in apps.Order(StringComparer.Ordinal))
            await ExecuteAsync(connection, transaction,
                "INSERT INTO tenant_apps (tenant_id, app_id, status) VALUES (@tenant, @app, 'enabled')",
                cancellationToken, ("tenant", tenantId), ("app", app));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO tenant_settings (tenant_id) VALUES (@tenant)", cancellationToken,
            ("tenant", tenantId));

        var invitationCode = InvitationToken.Issue(tenantId);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO invitations (id, tenant_id, email, role_ids, token_hash, status, invited_by, expires_at)
            VALUES (@id, @tenant, @email, @roles::jsonb, @hash, 'pending', @actor, now() + interval '7 days')
            """, cancellationToken, ("id", invitationId), ("tenant", tenantId), ("email", email),
            ("roles", JsonSerializer.Serialize(new[] { ownerRole })),
            ("hash", invitationCode.SecretHash), ("actor", actorSub));
        await AccessAuditWriter.WriteAsync(connection, transaction, tenantId, actorSub, "human",
            "access.tenant.provisioned", "tenant", tenantId.ToString("D"),
            "platform operator requested tenant provisioning", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (deliveryCoordinator is not null)
            await deliveryCoordinator.DeliverAsync(invitationId, tenantId, email,
                invitationCode.Value, null, new IdentityAdminActor(actorSub, actorAcr),
                cancellationToken);
        return new(CreateTenantStatus.Created, new CreatedTenant(tenantId, "provisioning", invitationId));
    }

    private static bool Valid(CreateTenantRequest request)
    {
        var name = request.Name?.Trim();
        var email = request.OwnerEmail?.Trim();
        var apps = request.InitialApps ?? [];
        return name is { Length: > 0 and <= 160 } &&
            request.Slug is { Length: >= 3 and <= 40 } slug && SlugPattern.IsMatch(slug) &&
            !ReservedSlugs.Contains(slug) &&
            request.Plan is { Length: > 0 and <= 40 } plan && PlanPattern.IsMatch(plan) &&
            email is { Length: > 0 and <= 254 } &&
            MailAddress.TryCreate(email, out var address) && address.Address == email &&
            apps.Length <= 20 && apps.All(app => app is { Length: > 0 and <= 40 } &&
                Regex.IsMatch(app, "^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant));
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
