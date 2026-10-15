using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Excellore.Access.Api;
using Excellore.Access.Application;
using Excellore.Access.Infrastructure;
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
public sealed class AccessGroupApiTests
{
    [Fact]
    public async Task Groups_enforce_tenant_membership_escalation_step_up_and_audited_mutations()
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
                "003_membership_index.sql", "005_audit_append_only.sql",
                "006_custom_role_lifecycle.sql", "007_group_role_assignment_uniqueness.sql" })
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
                new CatalogPermission("access.group.manage", "Manage groups", false, false, false, ["owner", "admin"]),
                new CatalogPermission("access.member.read", "Read members", true, false, false, ["owner"]),
                new CatalogPermission("access.member.invite", "Invite members", false, false, false, ["owner", "admin"])
            ]), platformActor));
        var ownerOne = Guid.NewGuid();
        var ownerTwo = Guid.NewGuid();
        var tenantAdmin = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var mirror = new IdentityMirrorService(appDb);
        foreach (var identity in new[] { ownerOne, ownerTwo, tenantAdmin, recipient })
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
        foreach (var (tenant, member, role) in new[]
        {
            (tenantOne, ownerOne, ownerRole), (tenantOne, tenantAdmin, adminRole),
            (tenantTwo, ownerTwo, ownerRole)
        })
        {
            await ExecuteAsync(admin, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
                ("tenant", tenant), ("user", member));
            await ExecuteAsync(admin, "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @actor)",
                ("id", Guid.NewGuid()), ("tenant", tenant), ("user", member),
                ("role", role), ("actor", platformActor));
        }
        await ExecuteAsync(admin, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
            ("tenant", tenantOne), ("user", recipient));
        var elevatedRole = Guid.NewGuid();
        var simpleRole = Guid.NewGuid();
        var foreignRole = Guid.NewGuid();
        foreach (var (id, name, privileged) in new[]
        {
            (elevatedRole, "elevated", true), (simpleRole, "simple", false)
        })
        {
            await ExecuteAsync(admin, "INSERT INTO roles (id, tenant_id, name, is_system, privileged, created_by) VALUES (@id, @tenant, @name, false, @privileged, @actor)",
                ("id", id), ("tenant", tenantOne), ("name", name),
                ("privileged", privileged), ("actor", platformActor));
        }
        await ExecuteAsync(admin, "INSERT INTO role_permissions (role_id, tenant_id, permission_key) VALUES (@role, @tenant, 'access.member.read')",
            ("role", elevatedRole), ("tenant", tenantOne));
        await ExecuteAsync(admin, "INSERT INTO role_permissions (role_id, tenant_id, permission_key) VALUES (@role, @tenant, 'access.app.access')",
            ("role", simpleRole), ("tenant", tenantOne));
        await ExecuteAsync(admin, "INSERT INTO roles (id, tenant_id, name, is_system, privileged, created_by) VALUES (@id, @tenant, 'foreign-role', false, false, @actor)",
            ("id", foreignRole), ("tenant", tenantTwo), ("actor", platformActor));

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
            var invitationsPath = $"/v1/tenants/{tenantOne:D}/invitations";
            var foreignInvitation = (Guid)(await ScalarAsync(admin,
                "SELECT id FROM invitations WHERE tenant_id = @tenant", ("tenant", tenantTwo)))!;
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Get, ownerOne,
                $"/v1/tenants/{tenantTwo:D}/invitations");
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Get, recipient,
                invitationsPath);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Delete, recipient,
                $"{invitationsPath}/{foreignInvitation:D}");
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerOne,
                $"/v1/tenants/{tenantTwo:D}/invitations/{foreignInvitation:D}");
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerOne,
                $"{invitationsPath}/{foreignInvitation:D}");
            using (var invitations = await SendAsync(client, HttpMethod.Get, ownerOne, invitationsPath))
            {
                Assert.Equal(HttpStatusCode.OK, invitations.StatusCode);
                var rows = await invitations.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(1, rows.GetArrayLength());
                Assert.DoesNotContain(foreignInvitation.ToString("D"), rows.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            var ownInvitation = (Guid)(await ScalarAsync(admin,
                "SELECT id FROM invitations WHERE tenant_id = @tenant", ("tenant", tenantOne)))!;
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Delete, ownerOne,
                $"{invitationsPath}/{ownInvitation:D}");
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerOne,
                $"{invitationsPath}/{ownInvitation:D}");
            var groupsPath = $"/v1/tenants/{tenantOne:D}/groups";
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Get, ownerOne,
                $"/v1/tenants/{tenantTwo:D}/groups");
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Get, recipient,
                groupsPath);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, ownerOne,
                $"/v1/tenants/{tenantTwo:D}/groups", new { name = "Foreign" });
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Post, ownerOne,
                groupsPath, new { name = "x" });
            using var created = await SendAsync(client, HttpMethod.Post, ownerOne,
                groupsPath, new { name = "Analysts", description = "Access review team" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var groupId = (await created.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("groupId").GetGuid();
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, ownerOne,
                groupsPath, new { name = "analysts" });
            using var second = await SendAsync(client, HttpMethod.Post, ownerOne,
                groupsPath, new { name = "Observers" });
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("groupId").GetGuid();
            using var foreign = await SendAsync(client, HttpMethod.Post, ownerTwo,
                $"/v1/tenants/{tenantTwo:D}/groups", new { name = "Foreign" });
            Assert.Equal(HttpStatusCode.Created, foreign.StatusCode);
            var foreignId = (await foreign.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("groupId").GetGuid();
            using var foreignGrant = await SendAsync(client, HttpMethod.Post, ownerTwo,
                $"/v1/tenants/{tenantTwo:D}/groups/{foreignId:D}/role-assignments",
                new { roleId = adminRole, reason = "foreign-grant" });
            Assert.Equal(HttpStatusCode.Created, foreignGrant.StatusCode);
            var foreignAssignmentId = (await foreignGrant.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("assignmentId").GetGuid();
            using (var listed = await SendAsync(client, HttpMethod.Get, ownerOne, groupsPath))
            {
                Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
                var rows = await listed.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(2, rows.GetArrayLength());
                Assert.DoesNotContain("Foreign", rows.ToString(), StringComparison.Ordinal);
            }

            var membersPath = $"{groupsPath}/{groupId:D}/members";
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Put, ownerOne,
                $"{groupsPath}/{foreignId:D}/members",
                new { userId = recipient, reason = "add-member" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerOne,
                $"{groupsPath}/{foreignId:D}/members",
                new { userId = recipient, reason = "remove-member" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Put, ownerOne,
                membersPath, new { userId = ownerOne, reason = "self" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Put, ownerOne,
                membersPath, new { userId = ownerTwo, reason = "foreign-member" });
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Put, ownerOne,
                membersPath, new { userId = recipient, reason = "person@example.invalid" });
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Put, ownerOne,
                membersPath, new { userId = recipient, reason = "add-member" });
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Put, ownerOne,
                membersPath, new { userId = recipient, reason = "add-member" });

            var rolesPath = $"{groupsPath}/{groupId:D}/role-assignments";
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, ownerOne,
                $"{groupsPath}/{foreignId:D}/role-assignments",
                new { roleId = simpleRole, reason = "grant-role" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, ownerOne,
                rolesPath, new { roleId = foreignRole, reason = "foreign-role" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, tenantAdmin,
                rolesPath, new { roleId = elevatedRole, reason = "grant-role" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, ownerOne,
                rolesPath, new { roleId = elevatedRole, reason = "grant-role" }, "1");
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, ownerOne,
                rolesPath, new { roleId = ownerRole, reason = "grant-owner" });
            using var roleGrant = await SendAsync(client, HttpMethod.Post, ownerOne,
                rolesPath, new { roleId = elevatedRole, reason = "grant-role" });
            Assert.Equal(HttpStatusCode.Created, roleGrant.StatusCode);
            var assignmentId = (await roleGrant.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("assignmentId").GetGuid();
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, ownerOne,
                rolesPath, new { roleId = elevatedRole, reason = "grant-role" });
            var resolved = await new AccessResolutionService(appDb).ResolveAsync(recipient, tenantOne);
            Assert.Contains("access.member.read", resolved.Permissions);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Put, tenantAdmin,
                membersPath, new { userId = ownerOne, reason = "group-escalation" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Put, ownerOne,
                membersPath, new { userId = tenantAdmin, reason = "add-member" }, "1");
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Put, ownerOne,
                membersPath, new { userId = tenantAdmin, reason = "add-member" });

            var secondMembers = $"{groupsPath}/{secondId:D}/members";
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Put, tenantAdmin,
                secondMembers, new { userId = ownerOne, reason = "add-member" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Delete, ownerOne,
                secondMembers, new { userId = ownerOne, reason = "self" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, ownerOne,
                $"{groupsPath}/{secondId:D}/role-assignments",
                new { roleId = simpleRole, reason = "self-group-role" });
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Delete, tenantAdmin,
                secondMembers, new { userId = ownerOne, reason = "remove-member" });

            var secondRoles = $"{groupsPath}/{secondId:D}/role-assignments";
            using var temporary = await SendAsync(client, HttpMethod.Post, ownerOne,
                secondRoles, new { roleId = simpleRole,
                    expiresAt = DateTimeOffset.UtcNow.AddDays(1), reason = "temporary" });
            Assert.Equal(HttpStatusCode.Created, temporary.StatusCode);
            var temporaryId = (await temporary.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("assignmentId").GetGuid();
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerOne,
                $"{rolesPath}/{temporaryId:D}", new { reason = "wrong-group" });
            await ExecuteAsync(admin, "UPDATE group_role_assignments SET expires_at = now() - interval '1 second' WHERE id = @id",
                ("id", temporaryId));
            using var replacement = await SendAsync(client, HttpMethod.Post, ownerOne,
                secondRoles, new { roleId = simpleRole, reason = "renewed" });
            Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
            Assert.NotEqual(temporaryId, (await replacement.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("assignmentId").GetGuid());
            Assert.NotNull(await ScalarAsync(admin,
                "SELECT revoked_at FROM group_role_assignments WHERE id = @id", ("id", temporaryId)));
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerOne,
                $"{groupsPath}/{foreignId:D}/role-assignments/{assignmentId:D}",
                new { reason = "foreign-group" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerOne,
                $"{rolesPath}/{foreignAssignmentId:D}", new { reason = "foreign-assignment" });
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Delete, ownerOne,
                membersPath, new { userId = recipient, reason = "remove-member" });
            resolved = await new AccessResolutionService(appDb).ResolveAsync(recipient, tenantOne);
            Assert.DoesNotContain("access.member.read", resolved.Permissions);
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Delete, ownerOne,
                $"{rolesPath}/{assignmentId:D}", new { reason = "role-ended" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, ownerOne,
                $"{rolesPath}/{assignmentId:D}", new { reason = "role-ended" });
            var legacyOwnerGroup = Guid.NewGuid();
            await ExecuteAsync(admin, "INSERT INTO groups (id, tenant_id, name) VALUES (@id, @tenant, 'legacy-owner-group')",
                ("id", legacyOwnerGroup), ("tenant", tenantOne));
            await ExecuteAsync(admin, "INSERT INTO group_members (tenant_id, group_id, user_id) VALUES (@tenant, @group, @user)",
                ("tenant", tenantOne), ("group", legacyOwnerGroup), ("user", recipient));
            await ExecuteAsync(admin, "INSERT INTO group_role_assignments (id, tenant_id, group_id, role_id, granted_by) VALUES (@id, @tenant, @group, @role, @actor)",
                ("id", Guid.NewGuid()), ("tenant", tenantOne), ("group", legacyOwnerGroup),
                ("role", ownerRole), ("actor", platformActor));
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Delete, ownerOne,
                $"{groupsPath}/{legacyOwnerGroup:D}/members",
                new { userId = recipient, reason = "ownership-change" });
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Access", previousConnection);
            Environment.SetEnvironmentVariable("Auth__Authority", previousAuthority);
        }

        Assert.Equal(0L, await CountAsync(admin,
            "group_members WHERE tenant_id = @tenant AND user_id = @user",
            ("tenant", tenantTwo), ("user", recipient)));
        Assert.True(await CountAsync(admin,
            "audit_events WHERE tenant_id = @tenant AND action LIKE 'access.group.%'",
            ("tenant", tenantOne)) >= 12);
        Assert.True(await CountAsync(admin,
            "outbox WHERE tenant_id = @tenant AND type = 'access.authz.changed.v1'",
            ("tenant", tenantOne)) >= 10);
        Assert.Equal(0L, await CountAsync(admin,
            "outbox WHERE tenant_id = @tenant AND payload::text LIKE '%Analysts%'",
            ("tenant", tenantOne)));
        Assert.Equal(1L, await CountAsync(admin,
            "group_role_assignments WHERE tenant_id = @tenant AND group_id = @group AND role_id = @role AND revoked_at IS NULL",
            ("tenant", tenantOne), ("group", (Guid)(await ScalarAsync(admin,
                "SELECT id FROM groups WHERE tenant_id = @tenant AND name = 'Observers'",
                ("tenant", tenantOne)))!), ("role", simpleRole)));
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
