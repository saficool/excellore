using System.Net;
using System.Text;
using Excellore.Access.Infrastructure;

namespace Excellore.Access.Tests;

public sealed class KeycloakIdentityAdminTests
{
    [Fact]
    public async Task Created_user_gets_required_actions_without_email_or_invitation_code_in_urls()
    {
        var userId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var handler = new StubHandler(async request =>
        {
            Assert.DoesNotContain("person@example.invalid", request.RequestUri!.ToString(),
                StringComparison.Ordinal);
            if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"access_token\":\"test-only-token\"}");
            if (request.Method == HttpMethod.Post &&
                request.RequestUri.AbsolutePath.EndsWith("/users", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("person@example.invalid", body, StringComparison.Ordinal);
                var created = new HttpResponseMessage(HttpStatusCode.Created);
                created.Headers.Location = new Uri($"https://identity.example.invalid/admin/realms/excellore/users/{userId:D}");
                return created;
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.EndsWith($"/users/{userId:D}/execute-actions-email",
                request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("redirect_uri=https%3A%2F%2Faccess.example.invalid%2Finvitations%2Faccept",
                request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("lifespan=86400", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.DoesNotContain(invitationId.ToString("D"), request.RequestUri.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("UPDATE_PASSWORD", await request.Content!.ReadAsStringAsync(),
                StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler);
        var admin = new KeycloakIdentityAdmin(http, Options(), new IdentityAdminCircuit());
        var actor = new IdentityAdminActor(Guid.NewGuid(), "2");
        var created = await admin.EnsureInvitationIdentityAsync("person@example.invalid",
            invitationId, actor);
        Assert.Equal(IdentityCreationStatus.Created, created.Status);
        Assert.Equal(userId, created.UserId);
        Assert.True(await admin.SendRequiredActionsAsync(userId, invitationId, actor));
        Assert.Equal(4, handler.Requests);
    }

    [Fact]
    public async Task Existing_identity_recovery_uses_only_opaque_username_in_lookup_url()
    {
        var invitationId = Guid.NewGuid();
        var username = $"invited-{invitationId:N}";
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"access_token\":\"test-only-token\"}"));
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains($"username={username}", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("person@example.invalid", request.RequestUri.ToString(),
                StringComparison.Ordinal);
            return Task.FromResult(Json(HttpStatusCode.OK, "[]"));
        });
        using var http = new HttpClient(handler);
        var result = await new KeycloakIdentityAdmin(http, Options(), new IdentityAdminCircuit())
            .EnsureInvitationIdentityAsync("person@example.invalid", invitationId,
                new IdentityAdminActor(Guid.NewGuid(), "2"));
        Assert.Equal(IdentityCreationStatus.Existing, result.Status);
    }

    [Fact]
    public async Task Private_http_authority_requires_an_exact_configured_host()
    {
        var handler = new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);
        var actor = new IdentityAdminActor(Guid.NewGuid(), "2");
        var invitation = Guid.NewGuid();
        var accepted = new IdentityAdminOptions(
            "http://keycloak.excellore.svc.cluster.local:8080/realms/excellore",
            "test-only-secret", "https://access.example.invalid/", false,
            "keycloak.excellore.svc.cluster.local");
        var rejected = accepted with
        {
            Authority = "http://keycloak.attacker.svc.cluster.local:8080/realms/excellore"
        };

        var acceptedResult = await new KeycloakIdentityAdmin(http, accepted,
            new IdentityAdminCircuit()).EnsureInvitationIdentityAsync(
                "person@example.invalid", invitation, actor);
        Assert.Equal(IdentityCreationStatus.Unavailable, acceptedResult.Status);
        Assert.Equal(2, handler.Requests);

        var rejectedResult = await new KeycloakIdentityAdmin(http, rejected,
            new IdentityAdminCircuit()).EnsureInvitationIdentityAsync(
                "person@example.invalid", invitation, actor);
        Assert.Equal(IdentityCreationStatus.Unavailable, rejectedResult.Status);
        Assert.Equal(2, handler.Requests);
    }

    private static IdentityAdminOptions Options() => new(
        "https://identity.example.invalid/realms/excellore", "test-only-secret",
        "https://access.example.invalid/", false);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return response(request);
        }
    }
}
