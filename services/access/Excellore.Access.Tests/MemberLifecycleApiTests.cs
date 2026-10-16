using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
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
public sealed class MemberLifecycleApiTests
{
    [Fact]
    public async Task Member_changes_preserve_tenant_owner_and_grant_boundaries()
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
                "003_membership_index.sql", "005_audit_append_only.sql", "006_custom_role_lifecycle.sql",
                "010_ownership_transfers.sql", "011_support_grants_rls.sql",
                "012_audit_export_requests.sql" })
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
                new CatalogPermission("access.app.access", "Open Access", false, false, false, ["owner", "admin", "member", "viewer"]),
                new CatalogPermission("access.member.suspend", "Suspend members", false, false, false, ["owner", "admin"]),
                new CatalogPermission("access.member.remove", "Remove members", false, false, false, ["owner", "admin"]),
                new CatalogPermission("access.role.manage", "Manage roles", true, false, false, ["owner"]),
                new CatalogPermission("access.tenant.transfer", "Transfer owner", true, true, false, ["owner"]),
                new CatalogPermission("access.settings.read", "Read settings", false, false, false, ["owner"]),
                new CatalogPermission("access.settings.manage", "Manage settings", true, false, false, ["owner"]),
                new CatalogPermission("access.request.create", "Request access", false, false, false, ["owner", "admin", "member"]),
                new CatalogPermission("access.request.approve", "Approve requests", false, false, false, ["owner", "admin"]),
                new CatalogPermission("access.tenant.delete", "Delete tenant", true, true, false, ["owner"]),
                new CatalogPermission("access.audit.read", "Read audit", false, false, false, ["owner"]),
                new CatalogPermission("access.supportgrant.manage", "Manage support", true, true, false, ["owner"])
            ]), platformActor));

        var owner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        var tenantAdmin = Guid.NewGuid();
        var target = Guid.NewGuid();
        var supportMember = Guid.NewGuid();
        var staff = Guid.NewGuid();
        var foreignOwner = Guid.NewGuid();
        var mirror = new IdentityMirrorService(appDb);
        foreach (var identity in new[] { owner, secondOwner, tenantAdmin, target,
            supportMember, foreignOwner, staff })
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
        var memberRole = (Guid)(await ScalarAsync(admin,
            "SELECT id FROM roles WHERE tenant_id IS NULL AND name = 'member'"))!;
        var viewerRole = (Guid)(await ScalarAsync(admin,
            "SELECT id FROM roles WHERE tenant_id IS NULL AND name = 'viewer'"))!;
        foreach (var (tenant, member, role) in new[]
        {
            (tenantOne, owner, ownerRole), (tenantOne, secondOwner, ownerRole),
            (tenantOne, tenantAdmin, adminRole), (tenantTwo, foreignOwner, ownerRole)
        })
        {
            await ExecuteAsync(admin, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
                ("tenant", tenant), ("user", member));
            await ExecuteAsync(admin, "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @actor)",
                ("id", Guid.NewGuid()), ("tenant", tenant), ("user", member),
                ("role", role), ("actor", platformActor));
        }
        await ExecuteAsync(admin, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
            ("tenant", tenantOne), ("user", target));
        await ExecuteAsync(admin, "INSERT INTO tenant_members (tenant_id, user_id, status, is_support) VALUES (@tenant, @user, 'active', true)",
            ("tenant", tenantOne), ("user", supportMember));
        var privilegedRole = Guid.NewGuid();
        await ExecuteAsync(admin, "INSERT INTO roles (id, tenant_id, name, privileged, created_by) VALUES (@id, @tenant, 'security-review', true, @actor)",
            ("id", privilegedRole), ("tenant", tenantOne), ("actor", platformActor));
        await ExecuteAsync(admin, "INSERT INTO role_permissions (role_id, tenant_id, permission_key) VALUES (@role, @tenant, 'access.role.manage')",
            ("role", privilegedRole), ("tenant", tenantOne));
        await ExecuteAsync(admin, "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @actor)",
            ("id", Guid.NewGuid()), ("tenant", tenantOne), ("user", target),
            ("role", privilegedRole), ("actor", platformActor));
        var group = Guid.NewGuid();
        await ExecuteAsync(admin, "INSERT INTO groups (id, tenant_id, name) VALUES (@id, @tenant, 'reviewers')",
            ("id", group), ("tenant", tenantOne));
        await ExecuteAsync(admin, "INSERT INTO group_members (tenant_id, group_id, user_id) VALUES (@tenant, @group, @user)",
            ("tenant", tenantOne), ("group", group), ("user", target));

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
            var requests = $"/v1/tenants/{tenantOne:D}/access-requests";
            var memberRequest = new { targetType = "role", targetKey = memberRole.ToString("D"),
                reason = "role-request" };
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, tenantAdmin,
                $"/v1/tenants/{tenantTwo:D}/access-requests", memberRequest);
            using var requested = await SendAsync(client, HttpMethod.Post, tenantAdmin,
                requests, memberRequest);
            Assert.Equal(HttpStatusCode.Created, requested.StatusCode);
            var memberRequestId = (await requested.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("requestId").GetGuid();
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, tenantAdmin,
                requests, memberRequest);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, tenantAdmin,
                $"{requests}/{memberRequestId:D}/approve", new { reason = "self-approve" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, owner,
                $"/v1/tenants/{tenantTwo:D}/access-requests/{memberRequestId:D}/approve",
                new { reason = "foreign-tenant" });
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post, owner,
                $"{requests}/{memberRequestId:D}/approve", new { reason = "approved-role" });
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, owner,
                $"{requests}/{memberRequestId:D}/approve", new { reason = "repeat-approval" });
            Assert.Contains("member", (await new AccessResolutionService(appDb)
                .ResolveAsync(tenantAdmin, tenantOne)).Roles);
            using var appRequested = await SendAsync(client, HttpMethod.Post, tenantAdmin,
                requests, new { targetType = "app", targetKey = "profile", reason = "app-request" });
            Assert.Equal(HttpStatusCode.Created, appRequested.StatusCode);
            var appRequestId = (await appRequested.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("requestId").GetGuid();
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, owner,
                $"{requests}/{appRequestId:D}/approve", new { reason = "platform-review" });
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post, owner,
                $"{requests}/{appRequestId:D}/reject", new { reason = "platform-review" });
            await ExpectAsync(HttpStatusCode.OK, client, HttpMethod.Get, owner, requests);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Get, owner,
                $"/v1/tenants/{tenantTwo:D}/access-requests");
            var basePath = $"/v1/tenants/{tenantOne:D}/members";
            var suspend = $"{basePath}:suspend";
            var reactivate = $"{basePath}:reactivate";
            var change = new { userId = target, reason = "security-review" };

            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, owner,
                $"/v1/tenants/{tenantTwo:D}/members:suspend", change);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, owner,
                $"/v1/tenants/{tenantTwo:D}/members:reactivate", change);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, owner,
                $"/v1/tenants/{tenantTwo:D}/members", change);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, target, suspend, change);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Delete, target, basePath,
                new { userId = tenantAdmin, reason = "unauthorized" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, owner, suspend,
                new { userId = foreignOwner, reason = "foreign-member" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, owner, reactivate,
                new { userId = foreignOwner, reason = "foreign-member" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Delete, owner, basePath,
                new { userId = foreignOwner, reason = "foreign-member" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, owner, suspend,
                new { userId = owner, reason = "self-change" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, owner, suspend,
                new { userId = supportMember, reason = "support-managed" });
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Post, owner, suspend,
                new { userId = target, reason = "person@example.invalid" });
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post, owner, suspend, change);
            Assert.False((await new AccessResolutionService(appDb).ResolveAsync(target, tenantOne)).Allowed);
            Assert.Equal("suspended", await ScalarAsync(admin,
                "SELECT membership_status FROM user_tenant_index WHERE tenant_id = @tenant AND user_id = @user",
                ("tenant", tenantOne), ("user", target)));
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, owner, suspend, change);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, tenantAdmin, reactivate, change);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, owner, reactivate, change, "1");
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post, owner, reactivate, change);
            Assert.Contains("access.role.manage",
                (await new AccessResolutionService(appDb).ResolveAsync(target, tenantOne)).Permissions);
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Delete, owner, basePath, change);
            Assert.False((await new AccessResolutionService(appDb).ResolveAsync(target, tenantOne)).Allowed);
            Assert.Equal("removed", await ScalarAsync(admin,
                "SELECT membership_status FROM user_tenant_index WHERE tenant_id = @tenant AND user_id = @user",
                ("tenant", tenantOne), ("user", target)));
            Assert.Equal(0L, await CountAsync(admin,
                "group_members WHERE tenant_id = @tenant AND user_id = @user",
                ("tenant", tenantOne), ("user", target)));
            Assert.Equal(0L, await CountAsync(admin,
                "role_assignments WHERE tenant_id = @tenant AND user_id = @user AND revoked_at IS NULL",
                ("tenant", tenantOne), ("user", target)));
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Delete, owner, basePath, change);
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, owner, reactivate, change);

            var ownerChange = new { userId = secondOwner, reason = "owner-review" };
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, tenantAdmin,
                suspend, ownerChange);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, owner,
                suspend, ownerChange, "1");
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post, owner,
                suspend, ownerChange);
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post, owner,
                reactivate, ownerChange);
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post, owner,
                suspend, ownerChange);

            // Simulate a legacy grant of the owner-only permission to exercise S4
            // with a non-owner actor while the sole active owner is the target.
            var legacyRole = Guid.NewGuid();
            await ExecuteAsync(admin, "INSERT INTO roles (id, tenant_id, name, privileged, created_by) VALUES (@id, @tenant, 'legacy-operator', true, @actor)",
                ("id", legacyRole), ("tenant", tenantOne), ("actor", platformActor));
            await ExecuteAsync(admin, "INSERT INTO role_permissions (role_id, tenant_id, permission_key) VALUES (@role, @tenant, 'access.tenant.transfer')",
                ("role", legacyRole), ("tenant", tenantOne));
            await ExecuteAsync(admin, "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @actor)",
                ("id", Guid.NewGuid()), ("tenant", tenantOne), ("user", tenantAdmin),
                ("role", legacyRole), ("actor", platformActor));
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Delete, tenantAdmin,
                basePath, new { userId = owner, reason = "last-owner" });
            Assert.True((await new AccessResolutionService(appDb).ResolveAsync(owner, tenantOne)).Allowed);
            Assert.Equal("active", await ScalarAsync(admin,
                "SELECT status FROM tenant_members WHERE tenant_id = @tenant AND user_id = @user",
                ("tenant", tenantOne), ("user", owner)));
            var nomination = $"/v1/tenants/{tenantOne:D}/ownership-transfers";
            var nominate = new { userId = tenantAdmin, reason = "owner-transition" };
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, owner,
                $"/v1/tenants/{tenantTwo:D}/ownership-transfers", nominate);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, tenantAdmin,
                nomination, new { userId = owner, reason = "takeover" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, owner,
                nomination, nominate, "1");
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, owner,
                nomination, new { userId = foreignOwner, reason = "foreign-member" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, owner,
                nomination, new { userId = owner, reason = "self-nomination" });
            using var created = await SendAsync(client, HttpMethod.Post, owner, nomination, nominate);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var transferId = (await created.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("transferId").GetGuid();
            Assert.Equal(1L, await CountAsync(admin,
                "role_assignments WHERE tenant_id = @tenant AND user_id = @user AND role_id = @role AND revoked_at IS NULL",
                ("tenant", tenantOne), ("user", owner), ("role", ownerRole)));
            var accept = $"{nomination}/{transferId:D}/accept";
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, foreignOwner,
                accept);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, owner,
                accept);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, tenantAdmin,
                $"/v1/tenants/{tenantTwo:D}/ownership-transfers/{transferId:D}/accept");
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post, tenantAdmin,
                accept);
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, tenantAdmin,
                accept);
            Assert.DoesNotContain("owner", (await new AccessResolutionService(appDb)
                .ResolveAsync(owner, tenantOne)).Roles);
            Assert.Contains("owner", (await new AccessResolutionService(appDb)
                .ResolveAsync(tenantAdmin, tenantOne)).Roles);
            Assert.Equal(1L, await CountAsync(admin,
                "audit_events WHERE tenant_id = @tenant AND action = 'access.ownership.accepted'",
                ("tenant", tenantOne)));
            var settings = $"/v1/tenants/{tenantOne:D}/settings";
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Patch, tenantAdmin,
                $"/v1/tenants/{tenantTwo:D}/settings",
                new { name = "Updated", reason = "tenant-review" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Patch, owner,
                settings, new { name = "Updated", reason = "tenant-review" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Patch, tenantAdmin,
                settings, new { name = "Updated", reason = "tenant-review" }, "1");
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Patch, tenantAdmin,
                settings, new { name = "Updated", reason = "person@example.invalid" });
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Patch, tenantAdmin,
                settings, new { slug = "second-tenant", reason = "tenant-review" });
            await ExpectAsync(HttpStatusCode.OK, client, HttpMethod.Patch, tenantAdmin,
                settings, new { name = "Updated", slug = "first-renamed", reason = "tenant-review" });
            Assert.Equal("first-renamed", await ScalarAsync(admin,
                "SELECT slug FROM tenants WHERE id = @tenant", ("tenant", tenantOne)));
            Assert.Equal(1L, await CountAsync(admin,
                "outbox WHERE tenant_id = @tenant AND type = 'tenant.updated.v1'",
                ("tenant", tenantOne)));
            var exports = $"/v1/tenants/{tenantOne:D}/audit-events/exports";
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, tenantAdmin,
                $"/v1/tenants/{tenantTwo:D}/audit-events/exports");
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, owner,
                exports);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, tenantAdmin,
                exports, null, "1");
            using var exportRequested = await SendAsync(client, HttpMethod.Post,
                tenantAdmin, exports);
            Assert.Equal(HttpStatusCode.Accepted, exportRequested.StatusCode);
            var exportId = (await exportRequested.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("exportId").GetGuid();
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Post, tenantAdmin,
                exports);
            await ExpectAsync(HttpStatusCode.OK, client, HttpMethod.Get, tenantAdmin,
                $"{exports}/{exportId:D}");
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Get, tenantAdmin,
                $"/v1/tenants/{tenantTwo:D}/audit-events/exports/{exportId:D}");
            Assert.Equal(1L, await CountAsync(admin,
                "audit_exports WHERE tenant_id = @tenant AND id = @id AND status = 'pending'",
                ("tenant", tenantOne), ("id", exportId)));
            var platformSupport = "/v1/platform/support-grants";
            var support = $"/v1/tenants/{tenantOne:D}/support-grants";
            var grantRequest = new { tenantId = tenantOne, roleId = viewerRole,
                durationMinutes = 60, reason = "case-review" };
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, staff,
                platformSupport, grantRequest);
            using var lowAcr = await SendAsync(client, HttpMethod.Post, staff,
                platformSupport, grantRequest, "1", platformRole: true);
            Assert.Equal(HttpStatusCode.Forbidden, lowAcr.StatusCode);
            using var supportRequested = await SendAsync(client, HttpMethod.Post, staff,
                platformSupport, grantRequest, platformRole: true);
            Assert.Equal(HttpStatusCode.Created, supportRequested.StatusCode);
            var grantId = (await supportRequested.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("grantId").GetGuid();
            using (var own = await SendAsync(client, HttpMethod.Get, staff,
                $"/v1/platform/tenants/{tenantOne:D}/support-requests", platformRole: true))
            {
                Assert.Equal(HttpStatusCode.OK, own.StatusCode);
                var mine = await own.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(grantId, mine[0].GetProperty("id").GetGuid());
                Assert.Equal("requested", mine[0].GetProperty("status").GetString());
            }
            using (var other = await SendAsync(client, HttpMethod.Get, staff,
                $"/v1/platform/tenants/{tenantTwo:D}/support-requests", platformRole: true))
            {
                Assert.Equal(HttpStatusCode.OK, other.StatusCode);
                Assert.Empty((await other.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
            }
            using var visibleGrants = await SendAsync(client, HttpMethod.Get,
                tenantAdmin, support);
            Assert.Equal(HttpStatusCode.OK, visibleGrants.StatusCode);
            var grants = await visibleGrants.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(Email(staff), grants[0].GetProperty("staffEmail").GetString());
            Assert.Equal("viewer", grants[0].GetProperty("roleName").GetString());
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Get, tenantAdmin,
                $"/v1/tenants/{tenantTwo:D}/support-grants");
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, owner,
                $"{support}/{grantId:D}/approve", new { reason = "case-review" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, tenantAdmin,
                $"/v1/tenants/{tenantTwo:D}/support-grants/{grantId:D}/approve",
                new { reason = "case-review" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, tenantAdmin,
                $"{support}/{grantId:D}/approve", new { reason = "case-review" }, "1");
            Assert.False((await new AccessResolutionService(appDb).ResolveAsync(staff, tenantOne)).Allowed);
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post, tenantAdmin,
                $"{support}/{grantId:D}/approve", new { reason = "case-review" });
            Assert.Contains("viewer", (await new AccessResolutionService(appDb)
                .ResolveAsync(staff, tenantOne)).Roles);
            await ExpectAsync(HttpStatusCode.OK, client, HttpMethod.Get, staff,
                $"/v1/tenants/{tenantOne:D}/me/access");
            Assert.Equal(1L, await CountAsync(admin,
                "audit_events WHERE tenant_id = @tenant AND action = 'access.supportgrant.used' AND target_id = @grant",
                ("tenant", tenantOne), ("grant", grantId.ToString("D"))));
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post, tenantAdmin,
                $"{support}/{grantId:D}/revoke", new { reason = "case-closed" });
            Assert.False((await new AccessResolutionService(appDb).ResolveAsync(staff, tenantOne)).Allowed);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Get, staff,
                $"/v1/tenants/{tenantOne:D}/me/access");
            Assert.Equal(1L, await CountAsync(admin,
                "audit_events WHERE tenant_id = @tenant AND action = 'access.supportgrant.approved'",
                ("tenant", tenantOne)));
            Assert.Equal(1L, await CountAsync(admin,
                "outbox WHERE tenant_id = @tenant AND type = 'access.supportgrant.created.v1'",
                ("tenant", tenantOne)));
            var createdPayload = (string)(await ScalarAsync(admin,
                "SELECT payload::text FROM outbox WHERE tenant_id = @tenant AND type = 'access.supportgrant.created.v1'",
                ("tenant", tenantOne)))!;
            var revokedPayload = (string)(await ScalarAsync(admin,
                "SELECT payload::text FROM outbox WHERE tenant_id = @tenant AND type = 'access.supportgrant.revoked.v1'",
                ("tenant", tenantOne)))!;
            Assert.Equal(grantId, AccessSupportGrantCreatedV1.Parse(
                System.Text.Encoding.UTF8.GetBytes(createdPayload)).Data!.GrantId);
            Assert.Equal(grantId, AccessSupportGrantRevokedV1.Parse(
                System.Text.Encoding.UTF8.GetBytes(revokedPayload)).Data!.GrantId);
            var deletion = $"/v1/tenants/{tenantOne:D}/deletion-requests";
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, tenantAdmin,
                $"/v1/tenants/{tenantTwo:D}/deletion-requests", new { reason = "closure" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, owner,
                deletion, new { reason = "closure" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post, tenantAdmin,
                deletion, new { reason = "closure" }, "1");
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Post, tenantAdmin,
                deletion, new { reason = "person@example.invalid" });
            await ExpectAsync(HttpStatusCode.Accepted, client, HttpMethod.Post, tenantAdmin,
                deletion, new { reason = "closure" });
            Assert.False((await new AccessResolutionService(appDb)
                .ResolveAsync(tenantAdmin, tenantOne)).Allowed);
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Post, tenantAdmin,
                deletion, new { reason = "closure" });
            Assert.Equal(1L, await CountAsync(admin,
                "outbox WHERE tenant_id = @tenant AND type = 'tenant.deletion.requested.v1'",
                ("tenant", tenantOne)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Access", previousConnection);
            Environment.SetEnvironmentVariable("Auth__Authority", previousAuthority);
        }
        Assert.Equal(6L, await CountAsync(admin,
            "outbox WHERE tenant_id = @tenant AND type LIKE 'tenant.member.%.v1'",
            ("tenant", tenantOne)));
        Assert.Equal(11L, await CountAsync(admin,
            "outbox WHERE tenant_id = @tenant AND type = 'access.authz.changed.v1'",
            ("tenant", tenantOne)));
        Assert.True(await CountAsync(admin,
            "audit_events WHERE tenant_id = @tenant AND action LIKE 'access.member.%.denied'",
            ("tenant", tenantOne)) >= 10);
        await VerifyEventsAsync(admin, tenantOne);
    }

    private static async Task VerifyEventsAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var command = new NpgsqlCommand("""
            SELECT type, payload::text FROM outbox
            WHERE tenant_id = @tenant AND type LIKE 'tenant.member.%.v1'
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync();
        var seen = new Dictionary<string, int>();
        while (await reader.ReadAsync())
        {
            var type = reader.GetString(0);
            var body = System.Text.Encoding.UTF8.GetBytes(reader.GetString(1));
            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.GetProperty("data").TryGetProperty("reason", out _));
            var eventTenant = type switch
            {
                TenantMemberSuspendedV1.EventType => TenantMemberSuspendedV1.Parse(body).Data!.TenantId,
                TenantMemberReactivatedV1.EventType => TenantMemberReactivatedV1.Parse(body).Data!.TenantId,
                TenantMemberRemovedV1.EventType => TenantMemberRemovedV1.Parse(body).Data!.TenantId,
                _ => throw new InvalidOperationException("Unexpected member event")
            };
            Assert.Equal(tenantId, eventTenant);
            seen[type] = seen.GetValueOrDefault(type) + 1;
        }
        Assert.Equal(3, seen[TenantMemberSuspendedV1.EventType]);
        Assert.Equal(2, seen[TenantMemberReactivatedV1.EventType]);
        Assert.Equal(1, seen[TenantMemberRemovedV1.EventType]);
    }

    private static string Email(Guid user) => $"user-{user:D}@example.invalid";

    private static async Task ExpectAsync(HttpStatusCode expected, HttpClient client,
        HttpMethod method, Guid user, string path, object? body = null, string acr = "2")
    {
        using var response = await SendAsync(client, method, user, path, body, acr);
        Assert.Equal(expected, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method,
        Guid user, string path, object? body = null, string acr = "2",
        bool platformRole = false)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Test-User", user.ToString("D"));
        request.Headers.Add("X-Test-Acr", acr);
        if (platformRole) request.Headers.Add("X-Test-Platform-Role", "platform-support");
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
            var claims = new List<Claim> {
                new Claim("sub", user.ToString("D")),
                new Claim("email", Email(user)),
                new Claim("email_verified", "true"),
                new Claim("acr", acr.Length == 0 ? "1" : acr)
            };
            if (Request.Headers["X-Test-Platform-Role"] == "platform-support")
                claims.Add(new Claim("platform_roles", "platform-support"));
            var identity = new ClaimsIdentity(claims, "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
