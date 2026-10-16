using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Excellore.Access.Api;
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
public sealed class PlatformTenantApiTests
{
    [Fact]
    public async Task Platform_roles_operate_metadata_and_apps_without_tenant_data_leakage()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var postgres = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(password).Build();
        await postgres.StartAsync();
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        await ExecAsync(admin, $"CREATE ROLE access_migrator LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
        await ExecAsync(admin, $"CREATE ROLE access_app LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS");
        await ExecAsync(admin, "GRANT USAGE,CREATE ON SCHEMA public TO access_migrator");
        await ExecAsync(admin, "GRANT USAGE ON SCHEMA public TO access_app");
        var migratorString = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = "access_migrator", Password = password }.ConnectionString;
        await using (var migrator = new NpgsqlConnection(migratorString))
        {
            await migrator.OpenAsync();
            foreach (var migration in new[] { "001_initial.sql", "003_membership_index.sql",
                         "005_audit_append_only.sql", "006_custom_role_lifecycle.sql",
                         "011_support_grants_rls.sql" })
                await ExecAsync(migrator, await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, migration)));
        }
        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();
        var member = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        var supportId = Guid.NewGuid();
        var securityId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO tenants(id,slug,name,status) VALUES
              (@one,'platform-one','First','active'),(@two,'platform-two','Second','active');
            INSERT INTO apps(id,name,core) VALUES
              ('access','Access',true),('profile','Profile',false);
            INSERT INTO permissions(key,app_id,description) VALUES
              ('access.app.access','access','Open Access'),
              ('profile.app.access','profile','Open Profile');
            INSERT INTO roles(id,tenant_id,name,is_system) VALUES (@role,NULL,'member',true);
            INSERT INTO role_permissions(role_id,tenant_id,permission_key) VALUES
              (@role,NULL,'access.app.access'),(@role,NULL,'profile.app.access');
            INSERT INTO identities(user_id,email,status) VALUES
              (@member,'member@example.invalid','active');
            INSERT INTO tenant_members(tenant_id,user_id,status) VALUES
              (@one,@member,'active'),(@two,@member,'active');
            INSERT INTO role_assignments(id,tenant_id,user_id,role_id,granted_by) VALUES
              (gen_random_uuid(),@one,@member,@role,@member),
              (gen_random_uuid(),@two,@member,@role,@member);
            INSERT INTO tenant_apps(tenant_id,app_id,status) VALUES
              (@one,'access','enabled'),(@two,'access','enabled');
            """, admin))
        {
            seed.Parameters.AddWithValue("one", tenantOne);
            seed.Parameters.AddWithValue("two", tenantTwo);
            seed.Parameters.AddWithValue("member", member);
            seed.Parameters.AddWithValue("role", roleId);
            await seed.ExecuteNonQueryAsync();
        }
        var appString = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = "access_app", Password = password }.ConnectionString;
        await using var appDb = NpgsqlDataSource.Create(appString);
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
            var root = "/v1/platform/tenants";
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Get,
                operatorId, null, root);
            await ExpectAsync(HttpStatusCode.OK, client, HttpMethod.Get,
                supportId, "platform-support", root);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Patch,
                supportId, "platform-support", $"{root}/{tenantOne:D}",
                new { name = "Changed", reason = "operator-change" });
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post,
                supportId, "platform-support", $"{root}/{tenantOne:D}/suspend",
                new { reason = "incident" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Get,
                supportId, "platform-support", $"/v1/tenants/{tenantOne:D}/members");
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Patch,
                operatorId, "platform-operator", $"{root}/{tenantOne:D}",
                new { name = "Renamed", reason = "operator-change" }, "1");
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Patch,
                operatorId, "platform-operator", $"{root}/{tenantOne:D}",
                new { name = "Renamed", slug = "platform-renamed", plan = "business",
                    reason = "operator-change" });
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Patch,
                operatorId, "platform-operator", $"{root}/{tenantTwo:D}",
                new { slug = "platform-renamed", reason = "slug-clash" });
            await ExpectAsync(HttpStatusCode.BadRequest, client, HttpMethod.Patch,
                operatorId, "platform-operator", $"{root}/{tenantOne:D}",
                new { slug = "owner@example.invalid", reason = "invalid" });
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Put,
                operatorId, "platform-operator", $"{root}/{tenantOne:D}/apps/unknown",
                new { enabled = true, reason = "app-enable" });
            await ExpectAsync(HttpStatusCode.Conflict, client, HttpMethod.Put,
                operatorId, "platform-operator", $"{root}/{tenantOne:D}/apps/access",
                new { enabled = false, reason = "app-disable" });
            var appPath = $"{root}/{tenantOne:D}/apps/profile";
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Put,
                operatorId, "platform-operator", appPath,
                new { enabled = true, reason = "app-enable" });
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Put,
                operatorId, "platform-operator", appPath,
                new { enabled = true, features = new { maxMembers = 25 },
                    reason = "feature-change" });
            var authority = new AccessResolutionService(appDb);
            Assert.Contains("profile.app.access",
                (await authority.ResolveAsync(member, tenantOne)).Permissions);
            Assert.DoesNotContain("profile.app.access",
                (await authority.ResolveAsync(member, tenantTwo)).Permissions);
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Put,
                operatorId, "platform-operator", appPath,
                new { enabled = false, reason = "app-disable" });
            Assert.DoesNotContain("profile.app.access",
                (await authority.ResolveAsync(member, tenantOne)).Permissions);
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post,
                securityId, "platform-security", $"{root}/{tenantOne:D}/reactivate",
                new { reason = "incident-clear" });
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post,
                securityId, "platform-security", $"{root}/{tenantOne:D}/suspend",
                new { reason = "incident" });
            Assert.False((await authority.ResolveAsync(member, tenantOne)).Allowed);
            Assert.True((await authority.ResolveAsync(member, tenantTwo)).Allowed);
            await ExpectAsync(HttpStatusCode.NoContent, client, HttpMethod.Post,
                operatorId, "platform-operator", $"{root}/{tenantOne:D}/reactivate",
                new { reason = "incident-clear" });
            Assert.True((await authority.ResolveAsync(member, tenantOne)).Allowed);
            using var detail = await SendAsync(client, HttpMethod.Get, supportId,
                "platform-support", $"{root}/{tenantOne:D}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var json = (await detail.Content.ReadFromJsonAsync<JsonElement>());
            Assert.Equal("Renamed", json.GetProperty("tenant").GetProperty("name").GetString());
            Assert.Equal("disabled", json.GetProperty("apps").EnumerateArray()
                .Single(app => app.GetProperty("id").GetString() == "profile")
                .GetProperty("status").GetString());
            await ExpectAsync(HttpStatusCode.OK, client, HttpMethod.Get,
                operatorId, "platform-operator", $"{root}/{tenantTwo:D}");
            await ExpectAsync(HttpStatusCode.NotFound, client, HttpMethod.Get,
                operatorId, "platform-operator", $"{root}/{Guid.NewGuid():D}");
            await ExpectAsync(HttpStatusCode.OK, client, HttpMethod.Get,
                operatorId, "platform-operator", "/v1/platform/tenants");
            await ExpectAsync(HttpStatusCode.OK, client, HttpMethod.Get,
                supportId, "platform-support", "/v1/platform/me");
            await ExpectAsync(HttpStatusCode.OK, client, HttpMethod.Get,
                supportId, "platform-support", "/v1/platform/permissions");
            await ExpectAsync(HttpStatusCode.OK, client, HttpMethod.Get,
                supportId, "platform-support", "/v1/platform/system-roles");
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Get,
                operatorId, "platform-operator", "/v1/platform/audit-events");
            using (var audit = await SendAsync(client, HttpMethod.Get, operatorId,
                "platform-auditor", "/v1/platform/audit-events"))
            {
                Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
                var events = await audit.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Contains(events.EnumerateArray(), item =>
                    item.GetProperty("action").GetString() == "access.platform.tenant.suspended");
            }
            await ExpectAsync(HttpStatusCode.Forbidden, client, HttpMethod.Post,
                supportId, "platform-support", $"{root}/{tenantOne:D}/deletion-requests",
                new { reason = "tenant-close" });
            using var deletion = await SendAsync(client, HttpMethod.Post, operatorId,
                "platform-operator", $"{root}/{tenantOne:D}/deletion-requests",
                new { reason = "tenant-close" });
            Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
            Assert.True((await deletion.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("dueAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(13));
            Assert.False((await authority.ResolveAsync(member, tenantOne)).Allowed);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Access", previousConnection);
            Environment.SetEnvironmentVariable("Auth__Authority", previousAuthority);
        }

        Assert.Equal(1L, await CountAsync(admin,
            "audit_events WHERE tenant_id IS NULL AND action='access.platform.tenant.suspended'"));
        Assert.Equal(1L, await CountAsync(admin,
            "audit_events WHERE tenant_id=@tenant AND action='access.platform.tenant.suspended'",
            ("tenant", tenantOne)));
        Assert.Equal(1L, await CountAsync(admin,
            "outbox WHERE tenant_id=@tenant AND type='tenant.app.disabled.v1'",
            ("tenant", tenantOne)));
        await using (var command = new NpgsqlCommand("""
            SELECT payload::text FROM outbox WHERE tenant_id=@tenant
              AND type='tenant.updated.v1' LIMIT 1
            """, admin))
        {
            command.Parameters.AddWithValue("tenant", tenantOne);
            var body = System.Text.Encoding.UTF8.GetBytes((string)(await command.ExecuteScalarAsync())!);
            Assert.Equal(tenantOne, TenantUpdatedV1.Parse(body).Data!.TenantId);
            Assert.DoesNotContain("Renamed", System.Text.Encoding.UTF8.GetString(body));
        }
        await using (var app = await appDb.OpenConnectionAsync())
        await using (var tx = await app.BeginTransactionAsync())
        {
            await ExecAsync(app, "SELECT set_config('app.tenant_id',@tenant,true)",
                ("tenant", tenantOne.ToString("D")));
            await using var update = new NpgsqlCommand("""
                UPDATE tenant_apps SET status='enabled'
                WHERE tenant_id=@other AND app_id='profile'
                """, app, tx);
            update.Parameters.AddWithValue("other", tenantTwo);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
            await tx.CommitAsync();
        }
    }

    private static async Task ExpectAsync(HttpStatusCode expected, HttpClient client,
        HttpMethod method, Guid user, string? role, string path, object? body = null,
        string acr = "2")
    {
        using var response = await SendAsync(client, method, user, role, path, body, acr);
        Assert.Equal(expected, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method,
        Guid user, string? role, string path, object? body = null, string acr = "2")
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Test-User", user.ToString("D"));
        request.Headers.Add("X-Test-Acr", acr);
        if (role is not null) request.Headers.Add("X-Test-Role", role);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string expression,
        params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {expression}", connection);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
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
            var role = Request.Headers["X-Test-Role"].ToString();
            var acr = Request.Headers["X-Test-Acr"].ToString();
            var claims = new List<Claim>
            {
                new("sub", user.ToString("D")),
                new("email", $"user-{user:D}@example.invalid"),
                new("email_verified", "true"),
                new("acr", acr.Length == 0 ? "1" : acr)
            };
            if (role.Length > 0) claims.Add(new Claim("platform_roles", role));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, "Test")));
        }
    }
}
