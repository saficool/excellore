using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Excellore.Access.Application;
using Excellore.Access.Api;
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

[CollectionDefinition("Access API host configuration", DisableParallelization = true)]
public sealed class AccessApiHostConfigurationCollection;

[Collection("Access API host configuration")]
public sealed class AccessTenantReadRoutesTests
{
    [Fact]
    public async Task Every_tenant_read_route_hides_foreign_tenants_and_roles()
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
            foreach (var migration in new[] { "001_initial.sql", "002_register_apps.sql", "003_membership_index.sql",
                "005_audit_append_only.sql", "006_custom_role_lifecycle.sql",
                "008_membership_identity.sql" })
                await ExecuteAsync(migrator, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, migration)));
        }

        var appString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_app", Password = appPassword
        }.ConnectionString;
        await using var appDb = NpgsqlDataSource.Create(appString);
        var actor = Guid.NewGuid();
        Assert.Equal(CatalogSyncResult.Updated,
            await new CatalogSyncService(appDb).SyncAsync("access", new PermissionCatalog("access", [
                new CatalogPermission("access.app.access", "Open Access", false, false, false, ["owner", "member"]),
                new CatalogPermission("access.role.read", "Read roles", false, false, false, ["owner"]),
                new CatalogPermission("access.role.manage", "Manage roles", true, false, false, ["owner"]),
                new CatalogPermission("access.member.read", "Read members", false, false, false, ["owner"]),
                new CatalogPermission("access.apps.read", "Read apps", false, false, false, ["owner"]),
                new CatalogPermission("access.audit.read", "Read audit", false, false, false, ["owner"]),
                new CatalogPermission("access.settings.read", "Read settings", false, false, false, ["owner"])
            ]), actor));
        await ExecuteAsync(admin, "INSERT INTO apps (id, name, core) VALUES ('reports', 'Reports', false)");
        Assert.Equal(CatalogSyncResult.Updated,
            await new CatalogSyncService(appDb).SyncAsync("reports", new PermissionCatalog("reports", [
                new CatalogPermission("reports.app.access", "Open reports", false, false, false, ["owner"])
            ]), actor));

        var userOne = Guid.NewGuid();
        var userTwo = Guid.NewGuid();
        var userThree = Guid.NewGuid();
        var mirror = new IdentityMirrorService(appDb);
        Assert.Equal(IdentityMirrorResult.Created, await mirror.UpsertAsync(
            new VerifiedIdentity(userOne, Email(userOne), true, null, null)));
        Assert.Equal(IdentityMirrorResult.Created, await mirror.UpsertAsync(
            new VerifiedIdentity(userTwo, Email(userTwo), true, null, null)));
        Assert.Equal(IdentityMirrorResult.Created, await mirror.UpsertAsync(
            new VerifiedIdentity(userThree, Email(userThree), true, null, null)));
        var provisioner = new TenantProvisioningService(appDb);
        var tenantOne = (await provisioner.CreateAsync(
            new("First", "first-tenant", "starter", "invite-one@example.invalid", []), actor)).Tenant!.TenantId;
        var tenantTwo = (await provisioner.CreateAsync(
            new("Second", "second-tenant", "starter", "invite-two@example.invalid", ["reports"]), actor)).Tenant!.TenantId;
        var ownerRole = (Guid)(await ScalarAsync(admin,
            "SELECT id FROM roles WHERE tenant_id IS NULL AND name = 'owner'"))!;
        var memberRole = (Guid)(await ScalarAsync(admin,
            "SELECT id FROM roles WHERE tenant_id IS NULL AND name = 'member'"))!;
        var firstRole = Guid.NewGuid();
        var secondRole = Guid.NewGuid();
        await ExecuteAsync(admin, "UPDATE tenants SET status = 'active' WHERE id IN (@one, @two)",
            ("one", tenantOne), ("two", tenantTwo));
        foreach (var (tenant, user, role, name) in new[]
        {
            (tenantOne, userOne, firstRole, "first-only"),
            (tenantTwo, userTwo, secondRole, "second-only")
        })
        {
            await ExecuteAsync(admin, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
                ("tenant", tenant), ("user", user));
            await ExecuteAsync(admin, "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @actor)",
                ("id", Guid.NewGuid()), ("tenant", tenant), ("user", user), ("role", ownerRole), ("actor", actor));
            await ExecuteAsync(admin, "INSERT INTO roles (id, tenant_id, name) VALUES (@id, @tenant, @name)",
                ("id", role), ("tenant", tenant), ("name", name));
        }
        await ExecuteAsync(admin, "INSERT INTO tenant_members (tenant_id, user_id, status) VALUES (@tenant, @user, 'active')",
            ("tenant", tenantOne), ("user", userThree));
        await ExecuteAsync(admin, "INSERT INTO role_assignments (id, tenant_id, user_id, role_id, granted_by) VALUES (@id, @tenant, @user, @role, @actor)",
            ("id", Guid.NewGuid()), ("tenant", tenantOne), ("user", userThree),
            ("role", memberRole), ("actor", actor));

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

            foreach (var path in new[] { "me/access", "roles", $"roles/{firstRole:D}",
                "permissions", "members", "apps", "audit-events", "settings" })
            {
                using var response = await GetAsync(client, userOne, tenantOne, path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var denied = await GetAsync(client, userTwo, tenantOne, path);
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
                using var otherTenant = await GetAsync(client, userOne, tenantTwo, path);
                Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
            }
            using (var basicAccess = await GetAsync(client, userThree, tenantOne, "me/access"))
                Assert.Equal(HttpStatusCode.OK, basicAccess.StatusCode);
            foreach (var path in new[] { "roles", $"roles/{firstRole:D}",
                "permissions", "members", "apps", "audit-events", "settings" })
            {
                using var lackingPermission = await GetAsync(client, userThree, tenantOne, path);
                Assert.Equal(HttpStatusCode.Forbidden, lackingPermission.StatusCode);
            }
            using (var foreignRole = await GetAsync(client, userOne, tenantOne, $"roles/{secondRole:D}"))
                Assert.Equal(HttpStatusCode.NotFound, foreignRole.StatusCode);
            using (var roles = await GetAsync(client, userOne, tenantOne, "roles"))
            {
                var body = await roles.Content.ReadAsStringAsync();
                Assert.Contains("first-only", body, StringComparison.Ordinal);
                Assert.DoesNotContain("second-only", body, StringComparison.Ordinal);
            }
            using (var permissions = await GetAsync(client, userOne, tenantOne, "permissions"))
            {
                var body = await permissions.Content.ReadAsStringAsync();
                Assert.Contains("access.role.read", body, StringComparison.Ordinal);
                Assert.DoesNotContain("reports.app.access", body, StringComparison.Ordinal);
            }
            using (var members = await GetAsync(client, userOne, tenantOne, "members"))
            {
                var body = await members.Content.ReadAsStringAsync();
                Assert.Contains(userThree.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(userTwo.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            }
            using (var apps = await GetAsync(client, userOne, tenantOne, "apps"))
            {
                var body = await apps.Content.ReadAsStringAsync();
                Assert.DoesNotContain("reports", body, StringComparison.Ordinal);
            }
            using (var apps = await GetAsync(client, userTwo, tenantTwo, "apps"))
            {
                var body = await apps.Content.ReadAsStringAsync();
                Assert.Contains("reports", body, StringComparison.Ordinal);
            }
            using (var own = await LookupAsync(client, userOne, tenantOne, userThree))
                Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            using (var foreign = await LookupAsync(client, userOne, tenantOne, userTwo))
                Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            using (var nonMember = await LookupAsync(client, userTwo, tenantOne, userThree))
                Assert.Equal(HttpStatusCode.NotFound, nonMember.StatusCode);
            using (var wrongTenant = await LookupAsync(client, userOne, tenantTwo, userThree))
                Assert.Equal(HttpStatusCode.NotFound, wrongTenant.StatusCode);
            using (var tenants = await GetMeTenantsAsync(client, userOne))
            {
                var body = await tenants.Content.ReadAsStringAsync();
                Assert.Contains(tenantOne.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(tenantTwo.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            }
            var limited = false;
            for (var attempt = 0; attempt < 70; attempt++)
            {
                using var response = await GetAsync(client, userOne, tenantOne, "me/access");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    limited = true;
                    break;
                }
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            Assert.True(limited, "The tenant route must enforce its per-user rate limit.");
            using (var independent = await GetAsync(client, userTwo, tenantTwo, "me/access"))
                Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Access", previousConnection);
            Environment.SetEnvironmentVariable("Auth__Authority", previousAuthority);
        }
        var classifier = new AccessPrivilegeClassifier(appDb);
        Assert.False(await classifier.IsPrivilegedAsync("access.role.read"));
        Assert.True(await classifier.IsPrivilegedAsync("access.role.manage"));
        Assert.True(await classifier.IsPrivilegedAsync("unknown.permission"));
    }

    private static string Email(Guid user) => $"user-{user:D}@example.invalid";

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, Guid user, Guid tenant, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/tenants/{tenant:D}/{path}");
        request.Headers.Add("X-Test-User", user.ToString("D"));
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> LookupAsync(HttpClient client, Guid user,
        Guid tenant, Guid target)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/tenants/{tenant:D}/members:lookup");
        request.Headers.Add("X-Test-User", user.ToString("D"));
        request.Content = JsonContent.Create(new { userId = target });
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetMeTenantsAsync(HttpClient client, Guid user)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me/tenants");
        request.Headers.Add("X-Test-User", user.ToString("D"));
        return client.SendAsync(request);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
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
            var identity = new ClaimsIdentity([
                new Claim("sub", user.ToString("D")),
                new Claim("email", Email(user)),
                new Claim("email_verified", "true"),
                new Claim("acr", "1")
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
