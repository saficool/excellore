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
using Excellore.Platform;
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
public sealed class RoleAssignmentApiTests
{
    [Fact]
    public async Task Assignments_enforce_tenant_escalation_self_owner_expiry_and_audit_rules()
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
        await ExecuteAsync(admin, $"CREATE ROLE access_worker LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE, CREATE ON SCHEMA public TO access_migrator");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO access_app,access_worker");
        var migratorString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_migrator", Password = migratorPassword
        }.ConnectionString;
        await using (var migrator = new NpgsqlConnection(migratorString))
        {
            await migrator.OpenAsync();
            foreach (var migration in new[] { "001_initial.sql", "002_register_apps.sql",
                "003_membership_index.sql", "005_audit_append_only.sql", "006_custom_role_lifecycle.sql",
                "011_support_grants_rls.sql", "012_audit_export_requests.sql",
                "013_worker_expiry.sql", "014_worker_identity_drift.sql",
                "015_role_assignment_batches.sql" })
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
                new CatalogPermission("access.assignment.manage", "Assign roles", false, false, false, ["owner", "admin"]),
                new CatalogPermission("access.role.manage", "Manage roles", true, false, false, ["owner", "admin"]),
                new CatalogPermission("access.member.read", "Read members", false, false, false, ["owner"])
            ]), platformActor));

        var ownerOne = Guid.NewGuid();
        var ownerTwo = Guid.NewGuid();
        var tenantAdmin = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var batchRecipient = Guid.NewGuid();
        var mirror = new IdentityMirrorService(appDb);
        foreach (var identity in new[] { ownerOne, ownerTwo, tenantAdmin, recipient, batchRecipient })
            Assert.Equal(IdentityMirrorResult.Created, await mirror.UpsertAsync(
                new VerifiedIdentity(identity, Email(identity), true, null, null)));
        var provisioner = new TenantProvisioningService(appDb);
        var tenantOne = (await provisioner.CreateAsync(new("First", "first-tenant", "starter",
            "invite-one@example.invalid", []), platformActor)).Tenant!.TenantId;
        var tenantTwo = (await provisioner.CreateAsync(new("Second", "second-tenant", "starter",
            "invite-two@example.invalid", []), platformActor)).Tenant!.TenantId;
        await ExecuteAsync(admin, "UPDATE tenants SET status = 'active' WHERE id IN (@one, @two)",
            ("one", tenantOne), ("two", tenantTwo));
        var ownerRole = (Guid)(await ScalarAsync(admin,
            "SELECT id FROM roles WHERE tenant_id IS NULL AND name = 'owner'"))!;
        var adminRole = (Guid)(await ScalarAsync(admin,
            "SELECT id FROM roles WHERE tenant_id IS NULL AND name = 'admin'"))!;
        var ownerAssignment = Guid.Empty;
        foreach (var (tenant, member, role) in new[]
        {
            (tenantOne, ownerOne, ownerRole), (tenantOne, tenantAdmin, adminRole),
            (tenantTwo, ownerTwo, ownerRole)
        })
        {
            await ExecuteAsync(admin, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
                ("tenant", tenant), ("user", member));
            var id = Guid.NewGuid();
            if (tenant == tenantOne && member == ownerOne) ownerAssignment = id;
            await ExecuteAsync(admin, "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @actor)",
                ("id", id), ("tenant", tenant), ("user", member), ("role", role),
                ("actor", platformActor));
        }
        await ExecuteAsync(admin, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
            ("tenant", tenantOne), ("user", recipient));
        await ExecuteAsync(admin, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
            ("tenant", tenantOne), ("user", batchRecipient));
        var foreignAssignment = Guid.NewGuid();
        await ExecuteAsync(admin, "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @actor)",
            ("id", foreignAssignment), ("tenant", tenantTwo), ("user", ownerTwo),
            ("role", adminRole), ("actor", platformActor));
        var elevatedRole = Guid.NewGuid();
        var simpleRole = Guid.NewGuid();
        var foreignRole = Guid.NewGuid();
        foreach (var (id, name, privileged) in new[]
        {
            (elevatedRole, "elevated", true), (simpleRole, "simple", false)
        })
            await ExecuteAsync(admin, "INSERT INTO roles (id, tenant_id, name, is_system, privileged, created_by) VALUES (@id, @tenant, @name, false, @privileged, @actor)",
                ("id", id), ("tenant", tenantOne), ("name", name),
                ("privileged", privileged), ("actor", platformActor));
        foreach (var key in new[] { "access.app.access", "access.role.manage", "access.member.read" })
            await ExecuteAsync(admin, "INSERT INTO role_permissions (role_id, tenant_id, permission_key) VALUES (@role, @tenant, @key)",
                ("role", elevatedRole), ("tenant", tenantOne), ("key", key));
        await ExecuteAsync(admin, "INSERT INTO role_permissions (role_id, tenant_id, permission_key) VALUES (@role, @tenant, 'access.app.access')",
            ("role", simpleRole), ("tenant", tenantOne));
        await ExecuteAsync(admin, "INSERT INTO roles (id, tenant_id, name, is_system, privileged, created_by) VALUES (@id, @tenant, 'foreign', false, false, @actor)",
            ("id", foreignRole), ("tenant", tenantTwo), ("actor", platformActor));

        var previousConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Access");
        var previousAuthority = Environment.GetEnvironmentVariable("Auth__Authority");
        var batchId = Guid.Empty;
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
            var path = $"/v1/tenants/{tenantOne:D}/role-assignments";
            var batchPath = $"/v1/tenants/{tenantOne:D}/role-assignment-batches";
            var batchRequest = new { roleId = simpleRole,
                targetUserIds = new[] { batchRecipient }, reason = "bulk-grant" };
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, ownerOne,
                $"/v1/tenants/{tenantTwo:D}/role-assignment-batches", batchRequest);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, tenantAdmin,
                batchPath, batchRequest);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, ownerOne,
                batchPath, batchRequest, "1");
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, ownerOne,
                batchPath, new { roleId = simpleRole, targetUserIds = new[] { ownerTwo },
                    reason = "foreign-member" });
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Post, ownerOne,
                batchPath, new { roleId = simpleRole,
                    targetUserIds = new[] { batchRecipient, batchRecipient }, reason = "duplicate" });
            using (var queued = await SendAsync(client, HttpMethod.Post, ownerOne,
                       batchPath, batchRequest))
            {
                Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
                batchId = (await queued.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("id").GetGuid();
            }
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, ownerOne,
                batchPath, batchRequest);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Get, ownerTwo,
                $"/v1/tenants/{tenantTwo:D}/role-assignment-batches/{batchId:D}");
            await ExpectAsync(HttpStatusCode.OK, client, HttpMethod.Get, ownerOne,
                $"{batchPath}/{batchId:D}");
            var elevated = new { userId = recipient, roleId = elevatedRole,
                reason = "support-access" };
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, ownerOne,
                $"/v1/tenants/{tenantTwo:D}/role-assignments", elevated);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, recipient,
                path, elevated);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, ownerOne,
                path, new { userId = ownerTwo, roleId = simpleRole, reason = "foreign-member" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, ownerOne,
                path, new { userId = recipient, roleId = foreignRole, reason = "foreign-role" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, ownerOne,
                path, new { userId = ownerOne, roleId = simpleRole, reason = "self" });
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, ownerOne,
                path, new { userId = recipient, roleId = ownerRole, reason = "owner" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, tenantAdmin,
                path, elevated);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, ownerOne,
                path, elevated, "1");
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Post, ownerOne,
                path, new { userId = recipient, roleId = simpleRole, reason = "" });
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Post, ownerOne,
                path, new { userId = recipient, roleId = simpleRole, reason = "person@example.invalid" });
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Post, ownerOne,
                path, new { userId = recipient, roleId = simpleRole,
                    expiresAt = DateTimeOffset.UtcNow.AddDays(-1), reason = "expired" });

            using var granted = await SendAsync(client, HttpMethod.Post, ownerOne, path, elevated);
            Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
            var assignmentId = (await granted.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("assignmentId").GetGuid();
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, ownerOne,
                path, elevated);
            var resolved = await new AccessResolutionService(appDb).ResolveAsync(recipient, tenantOne);
            Assert.Contains("access.role.manage", resolved.Permissions);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerOne,
                $"{path}/{foreignAssignment:D}", new { reason = "foreign-assignment" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Delete, ownerOne,
                $"{path}/{ownerAssignment:D}", new { reason = "owner-safety" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Delete, ownerOne,
                $"{path}/{assignmentId:D}", new { reason = "revoke" }, "1");
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Delete, ownerOne,
                $"{path}/{assignmentId:D}", new { reason = "revoke" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerOne,
                $"{path}/{assignmentId:D}", new { reason = "again" });
            resolved = await new AccessResolutionService(appDb).ResolveAsync(recipient, tenantOne);
            Assert.DoesNotContain("access.role.manage", resolved.Permissions);

            var simple = new { userId = recipient, roleId = simpleRole,
                expiresAt = DateTimeOffset.UtcNow.AddDays(1), reason = "temporary" };
            using var first = await SendAsync(client, HttpMethod.Post, ownerOne, path, simple, "1");
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("assignmentId").GetGuid();
            await ExecuteAsync(admin, "UPDATE role_assignments SET expires_at = now() - interval '1 second' WHERE id = @id",
                ("id", firstId));
            using var replacement = await SendAsync(client, HttpMethod.Post, ownerOne, path, simple, "1");
            Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
            Assert.NotEqual(firstId, (await replacement.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("assignmentId").GetGuid());
            Assert.NotNull(await ScalarAsync(admin,
                "SELECT revoked_at FROM role_assignments WHERE id = @id", ("id", firstId)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Access", previousConnection);
            Environment.SetEnvironmentVariable("Auth__Authority", previousAuthority);
        }

        Assert.Equal(5L, await ScalarAsync(admin,
            "SELECT authz_version FROM tenants WHERE id = @tenant", ("tenant", tenantOne)));
        Assert.Equal(4L, await CountAsync(admin,
            "outbox WHERE tenant_id = @tenant AND type = 'access.authz.changed.v1'", ("tenant", tenantOne)));
        Assert.Equal(5L, await CountAsync(admin,
            "outbox WHERE tenant_id = @tenant AND type LIKE 'access.assignment.%.v1'", ("tenant", tenantOne)));
        Assert.Equal(5L, await CountAsync(admin,
            "audit_events WHERE tenant_id = @tenant AND action IN ('access.assignment.granted','access.assignment.revoked')",
            ("tenant", tenantOne)));
        Assert.True(await CountAsync(admin,
            "audit_events WHERE tenant_id = @tenant AND action LIKE 'access.assignment.%.denied'",
            ("tenant", tenantOne)) >= 11);
        await VerifyEventsAsync(admin, tenantOne);
        await using var workerDb = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            container.GetConnectionString()) { Username = "access_worker", Password = appPassword }
            .ConnectionString);
        await using (var worker = await workerDb.OpenConnectionAsync())
        await using (var privilege = new NpgsqlCommand(
            "SELECT has_table_privilege(current_user,'role_assignment_batches','INSERT')", worker))
            Assert.False((bool)(await privilege.ExecuteScalarAsync())!);
        Assert.False(await new RoleAssignmentBatchProcessor(workerDb, new FixedTenant(tenantTwo))
            .ProcessOneAsync(Guid.NewGuid()));
        var processor = new RoleAssignmentBatchProcessor(workerDb, new FixedTenant(tenantOne));
        Assert.True(await processor.ProcessOneAsync(Guid.NewGuid()));
        Assert.False(await processor.ProcessOneAsync(Guid.NewGuid()));
        Assert.Equal(1L, await CountAsync(admin,
            "role_assignments WHERE tenant_id=@tenant AND user_id=@user AND role_id=@role AND revoked_at IS NULL",
            ("tenant", tenantOne), ("user", batchRecipient), ("role", simpleRole)));
        Assert.Equal(0L, await CountAsync(admin,
            "role_assignments WHERE tenant_id=@tenant AND user_id=@user AND role_id=@role AND revoked_at IS NULL",
            ("tenant", tenantTwo), ("user", batchRecipient), ("role", simpleRole)));
        Assert.Equal(1L, await CountAsync(admin,
            "role_assignment_batches WHERE id=@id AND status='completed' AND cursor=1 AND succeeded=1 AND failed=0",
            ("id", batchId)));
        Assert.Equal(1L, await CountAsync(admin,
            "audit_events WHERE tenant_id=@tenant AND target_id=@id AND action='access.assignment.batch.granted'",
            ("tenant", tenantOne), ("id", batchId.ToString("D"))));
        await using (var worker = await workerDb.OpenConnectionAsync())
        await using (var transaction = await worker.BeginTransactionAsync())
        {
            await ExecuteAsync(worker, "SELECT set_config('app.tenant_id',@tenant,true)",
                ("tenant", tenantTwo.ToString("D")));
            Assert.Equal(0L, await CountAsync(worker,
                "role_assignment_batches WHERE id=@id", ("id", batchId)));
            await transaction.CommitAsync();
        }
        var revokedOwnerBatch = Guid.NewGuid();
        await ExecuteAsync(admin, """
            INSERT INTO role_assignment_batches
              (id,tenant_id,requested_by,role_id,target_user_ids,reason)
            VALUES (@id,@tenant,@actor,@role,ARRAY[@target]::uuid[],'owner-race')
            """, ("id", revokedOwnerBatch), ("tenant", tenantOne),
            ("actor", ownerOne), ("role", simpleRole), ("target", tenantAdmin));
        await ExecuteAsync(admin,
            "UPDATE role_assignments SET revoked_at=now() WHERE id=@id",
            ("id", ownerAssignment));
        Assert.True(await processor.ProcessOneAsync(Guid.NewGuid()));
        Assert.Equal(1L, await CountAsync(admin,
            "role_assignment_batches WHERE id=@id AND status='completed' AND cursor=1 AND succeeded=0 AND failed=1",
            ("id", revokedOwnerBatch)));
        Assert.Equal(0L, await CountAsync(admin,
            "role_assignments WHERE tenant_id=@tenant AND user_id=@user AND role_id=@role AND revoked_at IS NULL",
            ("tenant", tenantOne), ("user", tenantAdmin), ("role", simpleRole)));
        Assert.Equal(6L, await ScalarAsync(admin,
            "SELECT authz_version FROM tenants WHERE id=@tenant", ("tenant", tenantOne)));
    }

    private static async Task VerifyEventsAsync(NpgsqlConnection connection, Guid tenant)
    {
        await using var command = new NpgsqlCommand("""
            SELECT type, payload::text FROM outbox
            WHERE tenant_id = @tenant AND type LIKE 'access.assignment.%.v1'
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await command.ExecuteReaderAsync();
        var granted = 0;
        var revoked = 0;
        var permanent = 0;
        while (await reader.ReadAsync())
        {
            var type = reader.GetString(0);
            var body = Encoding.UTF8.GetBytes(reader.GetString(1));
            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.GetProperty("data").TryGetProperty("reason", out _));
            if (!json.RootElement.GetProperty("data").TryGetProperty("expiresAt", out _))
                permanent++;
            if (type == AccessAssignmentGrantedV1.EventType)
            {
                Assert.Equal(tenant, AccessAssignmentGrantedV1.Parse(body).Data!.TenantId);
                granted++;
            }
            else
            {
                Assert.Equal(AccessAssignmentRevokedV1.EventType, type);
                Assert.Equal(tenant, AccessAssignmentRevokedV1.Parse(body).Data!.TenantId);
                revoked++;
            }
        }
        Assert.Equal(3, granted);
        Assert.Equal(2, revoked);
        Assert.Equal(2, permanent);
    }

    private static string Email(Guid user) => $"user-{user:D}@example.invalid";

    private sealed class FixedTenant(Guid tenantId) : ITenantIdAccessor
    {
        public Guid TenantId { get; } = tenantId;
    }

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
