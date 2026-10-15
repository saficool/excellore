using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Excellore.Access.Api;
using Excellore.Access.Application;
using Excellore.Access.Infrastructure;
using Excellore.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Access.Tests;

[Collection("Access API host configuration")]
public sealed class CustomRoleApiTests
{
    [Fact]
    public async Task Custom_roles_enforce_boundaries_content_limits_step_up_and_audited_lifecycle()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"CREATE ROLE access_migrator LOGIN PASSWORD '{migratorPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, $"CREATE ROLE access_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE, CREATE ON SCHEMA public TO access_migrator");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO access_app");
        var migratorString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_migrator", Password = migratorPassword
        }.ConnectionString;
        await using (var migrator = new NpgsqlConnection(migratorString))
        {
            await migrator.OpenAsync();
            foreach (var migration in new[] { "001_initial.sql", "002_register_apps.sql",
                "003_membership_index.sql", "005_audit_append_only.sql", "006_custom_role_lifecycle.sql" })
                await ExecuteAsync(migrator, await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, migration)));
        }
        var appString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_app", Password = appPassword
        }.ConnectionString;
        await using var appDb = NpgsqlDataSource.Create(appString);
        var platformActor = Guid.NewGuid();
        Assert.Equal(CatalogSyncResult.Updated,
            await new CatalogSyncService(appDb).SyncAsync("access", new PermissionCatalog("access", [
                new CatalogPermission("access.app.access", "Open Access", false, false, false, ["owner", "admin"]),
                new CatalogPermission("access.role.read", "Read roles", false, false, false, ["owner", "admin"]),
                new CatalogPermission("access.role.manage", "Manage roles", true, false, false, ["owner", "admin"]),
                new CatalogPermission("access.member.read", "Read members", false, false, false, ["owner"]),
                new CatalogPermission("access.tenant.delete", "Delete tenant", true, true, false, ["owner"]),
                new CatalogPermission("access.old.feature", "Old feature", false, false, true, [])
            ]), platformActor));
        await ExecuteAsync(admin, "INSERT INTO apps (id, name, core) VALUES ('reports', 'Reports', false)");
        Assert.Equal(CatalogSyncResult.Updated,
            await new CatalogSyncService(appDb).SyncAsync("reports", new PermissionCatalog("reports", [
                new CatalogPermission("reports.app.access", "Open Reports", false, false, false, ["owner"])
            ]), platformActor));

        var ownerOne = Guid.NewGuid();
        var ownerTwo = Guid.NewGuid();
        var tenantAdmin = Guid.NewGuid();
        var mirror = new IdentityMirrorService(appDb);
        foreach (var user in new[] { ownerOne, ownerTwo, tenantAdmin })
            Assert.Equal(IdentityMirrorResult.Created, await mirror.UpsertAsync(
                new VerifiedIdentity(user, Email(user), true, null, null)));
        var provisioner = new TenantProvisioningService(appDb);
        var tenantOne = (await provisioner.CreateAsync(new("First", "first-tenant", "starter",
            "invite-one@example.invalid", []), platformActor)).Tenant!.TenantId;
        var tenantTwo = (await provisioner.CreateAsync(new("Second", "second-tenant", "starter",
            "invite-two@example.invalid", ["reports"]), platformActor)).Tenant!.TenantId;
        await ExecuteAsync(admin, "UPDATE tenants SET status = 'active' WHERE id IN (@one, @two)",
            ("one", tenantOne), ("two", tenantTwo));
        await ExecuteAsync(admin, "UPDATE tenant_apps SET features = '{\"maxCustomRoles\":1}'::jsonb WHERE tenant_id = @tenant AND app_id = 'access'",
            ("tenant", tenantOne));
        var ownerRole = (Guid)(await ScalarAsync(admin,
            "SELECT id FROM roles WHERE tenant_id IS NULL AND name = 'owner'"))!;
        var adminRole = (Guid)(await ScalarAsync(admin,
            "SELECT id FROM roles WHERE tenant_id IS NULL AND name = 'admin'"))!;
        foreach (var (tenant, user, role) in new[]
        {
            (tenantOne, ownerOne, ownerRole),
            (tenantOne, tenantAdmin, adminRole),
            (tenantTwo, ownerTwo, ownerRole)
        })
        {
            await ExecuteAsync(admin, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
                ("tenant", tenant), ("user", user));
            await ExecuteAsync(admin, "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @actor)",
                ("id", Guid.NewGuid()), ("tenant", tenant), ("user", user),
                ("role", role), ("actor", platformActor));
        }

        var previousConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Access");
        var previousAuthority = Environment.GetEnvironmentVariable("Auth__Authority");
        Environment.SetEnvironmentVariable("ConnectionStrings__Access", appString);
        Environment.SetEnvironmentVariable("Auth__Authority", "https://issuer.example.invalid/realms/excellore");
        try
        {
            using var factory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(web =>
                web.ConfigureTestServices(services => services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "Test";
                        options.DefaultChallengeScheme = "Test";
                    }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { })));
            using var client = factory.CreateClient();
            var basePath = $"/v1/tenants/{tenantOne:D}/roles";
            var request = new { name = "Reviewers", description = "Review access", permissions = new[]
                { "access.app.access", "access.role.read" } };

            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, ownerOne,
                $"/v1/tenants/{tenantTwo:D}/roles", request);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, ownerOne,
                basePath, request, "1");
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Post, ownerOne,
                basePath, new { name = "Dangerous", permissions = new[] { "access.tenant.delete" } });
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Post, ownerOne,
                basePath, new { name = "Disabled", permissions = new[] { "reports.app.access" } });
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Post, ownerOne,
                basePath, new { name = "Deprecated", permissions = new[] { "access.old.feature" } });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, tenantAdmin,
                basePath, new { name = "Escalated", permissions = new[] { "access.member.read" } });
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, ownerOne,
                basePath, new { name = "owner", permissions = Array.Empty<string>() });

            using var created = await SendAsync(client, HttpMethod.Post, ownerOne, basePath, request);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
            var roleId = createdJson.GetProperty("roleId").GetGuid();
            using var foreign = await SendAsync(client, HttpMethod.Post, ownerTwo,
                $"/v1/tenants/{tenantTwo:D}/roles", request);
            Assert.Equal(HttpStatusCode.Created, foreign.StatusCode);
            var foreignRoleId = (await foreign.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("roleId").GetGuid();
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Put, ownerOne,
                $"{basePath}/{foreignRoleId:D}", request);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerOne,
                $"{basePath}/{foreignRoleId:D}");
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, ownerOne,
                basePath, new { name = "reviewers", permissions = Array.Empty<string>() });
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, ownerOne,
                basePath, new { name = "Second Role", permissions = Array.Empty<string>() });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Put, ownerTwo,
                $"{basePath}/{roleId:D}", request);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerTwo,
                $"{basePath}/{roleId:D}");
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Put, ownerOne,
                $"{basePath}/{roleId:D}", new { name = "Reviewers", description = "Review access",
                    permissions = new[] { "access.app.access", "access.role.manage" } });
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Put, ownerOne,
                $"{basePath}/{roleId:D}", new { name = "Reviewers", description = "Review access",
                    permissions = new[] { "access.app.access", "access.role.manage" } });
            using (var role = await SendAsync(client, HttpMethod.Get, ownerOne,
                $"{basePath}/{roleId:D}"))
            {
                Assert.Equal(HttpStatusCode.OK, role.StatusCode);
                var body = await role.Content.ReadAsStringAsync();
                Assert.Contains("\"privileged\":true", body, StringComparison.Ordinal);
            }

            var assignment = Guid.NewGuid();
            await ExecuteAsync(admin, "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @actor)",
                ("id", assignment), ("tenant", tenantOne), ("user", tenantAdmin),
                ("role", roleId), ("actor", ownerOne));
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Delete, ownerOne,
                $"{basePath}/{roleId:D}");
            await ExecuteAsync(admin, "UPDATE role_assignments SET revoked_at = now() WHERE id = @id",
                ("id", assignment));
            var group = Guid.NewGuid();
            await ExecuteAsync(admin, "INSERT INTO groups (id, tenant_id, name) VALUES (@id, @tenant, 'test-group')",
                ("id", group), ("tenant", tenantOne));
            var groupAssignment = Guid.NewGuid();
            await ExecuteAsync(admin, "INSERT INTO group_role_assignments (id, tenant_id, group_id, role_id, granted_by) VALUES (@id, @tenant, @group, @role, @actor)",
                ("id", groupAssignment), ("tenant", tenantOne), ("group", group),
                ("role", roleId), ("actor", ownerOne));
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Delete, ownerOne,
                $"{basePath}/{roleId:D}");
            await ExecuteAsync(admin, "UPDATE group_role_assignments SET revoked_at = now() WHERE id = @id",
                ("id", groupAssignment));
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Delete, ownerOne,
                $"{basePath}/{roleId:D}");
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Get, ownerOne,
                $"{basePath}/{roleId:D}");
            using var recreated = await SendAsync(client, HttpMethod.Post, ownerOne,
                basePath, request);
            Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);
            Assert.NotEqual(roleId, (await recreated.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("roleId").GetGuid());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Access", previousConnection);
            Environment.SetEnvironmentVariable("Auth__Authority", previousAuthority);
        }

        Assert.Equal(1L, await CountAsync(admin,
            "roles WHERE tenant_id = @tenant AND deleted_at IS NULL AND NOT is_system", ("tenant", tenantOne)));
        Assert.Equal(1L, await CountAsync(admin,
            "roles WHERE tenant_id = @tenant AND deleted_at IS NOT NULL", ("tenant", tenantOne)));
        Assert.Equal(5L, await ScalarAsync(admin,
            "SELECT authz_version FROM tenants WHERE id = @tenant", ("tenant", tenantOne)));
        Assert.Equal(4L, await CountAsync(admin,
            "outbox WHERE tenant_id = @tenant AND type = 'access.authz.changed.v1'", ("tenant", tenantOne)));
        Assert.Equal(0L, await CountAsync(admin,
            "outbox WHERE tenant_id = @tenant AND payload::text LIKE '%Reviewers%'", ("tenant", tenantOne)));
        Assert.Equal(4L, await CountAsync(admin,
            "outbox WHERE tenant_id = @tenant AND type LIKE 'access.role.%.v1'", ("tenant", tenantOne)));
        await VerifyRoleEventsAsync(admin, tenantOne);
        Assert.Equal(4L, await CountAsync(admin,
            "audit_events WHERE tenant_id = @tenant AND action IN ('access.role.created','access.role.updated','access.role.deleted')",
            ("tenant", tenantOne)));
        Assert.True(await CountAsync(admin,
            "audit_events WHERE tenant_id = @tenant AND action LIKE 'access.role.%.denied'",
            ("tenant", tenantOne)) >= 7);
        Assert.Equal(1L, await CountAsync(admin,
            "audit_events WHERE tenant_id = @tenant AND action = 'access.role.updated' AND before IS NOT NULL AND after IS NOT NULL AND acr = '2'",
            ("tenant", tenantOne)));
        await VerifyVersionTwoAuditChainAsync(admin, tenantOne);
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(admin,
            "UPDATE audit_events SET reason = 'changed' WHERE tenant_id = @tenant", ("tenant", tenantOne)));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(admin,
            "DELETE FROM audit_events WHERE tenant_id = @tenant", ("tenant", tenantOne)));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(admin,
            "TRUNCATE audit_events"));
    }

    private static async Task VerifyRoleEventsAsync(NpgsqlConnection connection, Guid tenant)
    {
        await using var command = new NpgsqlCommand("""
            SELECT type, payload::text FROM outbox
            WHERE tenant_id = @tenant AND type LIKE 'access.role.%.v1'
            ORDER BY created_at, id
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await command.ExecuteReaderAsync();
        var seen = new List<string>();
        while (await reader.ReadAsync())
        {
            var type = reader.GetString(0);
            var body = Encoding.UTF8.GetBytes(reader.GetString(1));
            var parsedTenant = type switch
            {
                AccessRoleCreatedV1.EventType => AccessRoleCreatedV1.Parse(body).Data!.TenantId,
                AccessRoleUpdatedV1.EventType => AccessRoleUpdatedV1.Parse(body).Data!.TenantId,
                AccessRoleDeletedV1.EventType => AccessRoleDeletedV1.Parse(body).Data!.TenantId,
                _ => throw new InvalidOperationException("Unexpected role event type.")
            };
            Assert.Equal(tenant, parsedTenant);
            seen.Add(type);
        }
        Assert.Equal(2, seen.Count(type => type == AccessRoleCreatedV1.EventType));
        Assert.Contains(AccessRoleUpdatedV1.EventType, seen);
        Assert.Contains(AccessRoleDeletedV1.EventType, seen);
    }

    private static async Task VerifyVersionTwoAuditChainAsync(NpgsqlConnection connection, Guid tenant)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id, tenant_id, occurred_at, actor_sub, actor_type, action, target_type,
              target_id, reason, before::text, after::text, acr, prev_hash, hash, hash_version
            FROM audit_events WHERE tenant_id = @tenant ORDER BY occurred_at, id
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await command.ExecuteReaderAsync();
        byte[]? previous = null;
        var count = 0;
        while (await reader.ReadAsync())
        {
            Assert.Equal((short)2, reader.GetInt16(14));
            Assert.Equal(previous, reader.IsDBNull(12) ? null : reader.GetFieldValue<byte[]>(12));
            var record = JsonSerializer.Serialize(new
            {
                Version = 2,
                Id = reader.GetGuid(0),
                TenantId = reader.GetGuid(1),
                OccurredAt = reader.GetFieldValue<DateTimeOffset>(2),
                ActorSub = reader.GetGuid(3),
                ActorType = reader.GetString(4),
                Action = reader.GetString(5),
                TargetType = reader.GetString(6),
                TargetId = reader.GetString(7),
                Reason = reader.GetString(8),
                Before = reader.IsDBNull(9) ? null : reader.GetString(9),
                After = reader.IsDBNull(10) ? null : reader.GetString(10),
                Acr = reader.IsDBNull(11) ? null : reader.GetString(11)
            });
            var body = Encoding.UTF8.GetBytes(record);
            var input = new byte[(previous?.Length ?? 0) + body.Length];
            previous?.CopyTo(input, 0);
            body.CopyTo(input, previous?.Length ?? 0);
            previous = SHA256.HashData(input);
            Assert.Equal(previous, reader.GetFieldValue<byte[]>(13));
            count++;
        }
        Assert.True(count >= 12);
    }

    private static string Email(Guid user) => $"user-{user:D}@example.invalid";

    private static async Task ExpectAsync(HttpStatusCode expected, HttpClient client,
        HttpMethod method, Guid user, string path, object? body = null, string acr = "2")
    {
        using var response = await SendAsync(client, method, user, path, body, acr);
        Assert.Equal(expected, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method,
        Guid user, string path, object? body = null, string acr = "2")
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Test-User", user.ToString("D"));
        request.Headers.Add("X-Test-Acr", acr);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string tableAndWhere,
        params (string Name, object Value)[] parameters) =>
        (long)(await ScalarAsync(connection, $"SELECT count(*) FROM {tableAndWhere}", parameters) ?? 0L);

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(
            options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers["X-Test-User"], out var user))
                return Task.FromResult(AuthenticateResult.NoResult());
            var acr = Request.Headers["X-Test-Acr"].ToString();
            var identity = new ClaimsIdentity([
                new Claim("sub", user.ToString("D")),
                new Claim("email", Email(user)),
                new Claim("email_verified", "true"),
                new Claim("acr", acr.Length == 0 ? "1" : acr)
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
