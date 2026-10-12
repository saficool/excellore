using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Excellore.Authz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Excellore.Authz.Tests;

public class AccessAuthzClientTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public async Task Resolve_posts_ids_in_body_and_machine_token_in_header()
    {
        var token = RandomValue();
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://access.internal/internal/v1/authz/resolve", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(token, request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(UserId, body.RootElement.GetProperty("userId").GetGuid());
            Assert.Equal(TenantId, body.RootElement.GetProperty("tenantId").GetGuid());
            return Json($$"""{"tenantId":"{{TenantId}}","tenantStatus":"active","membershipStatus":"active","permissions":["profile.app.access"]}""");
        });
        var source = new HttpAccessAuthzSource(new Factory(handler), new StaticToken(token));

        var snapshot = await source.ResolveFreshAsync(UserId, TenantId, CancellationToken.None);

        Assert.True(snapshot.IsActiveMember);
        Assert.Contains("profile.app.access", snapshot.Permissions);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Explicit_deny_has_no_grants()
    {
        var source = Source($$"""{"tenantId":"{{TenantId}}","decision":"deny","permissions":[]}""");
        var snapshot = await source.ResolveFreshAsync(UserId, TenantId, CancellationToken.None);
        Assert.Equal(AuthzDecision.Deny, snapshot.Decision);
        Assert.Empty(snapshot.Permissions);
    }

    [Theory]
    [InlineData("{\"tenantId\":\"00000000-0000-0000-0000-000000000000\",\"decision\":\"deny\",\"permissions\":[]}")]
    [InlineData("{\"decision\":\"deny\",\"permissions\":[]}")]
    [InlineData("{\"tenantId\":\"00000000-0000-0000-0000-000000000000\",\"permissions\":[\"profile.app.access\"]}")]
    [InlineData("not json")]
    public async Task Invalid_or_wrong_tenant_response_fails_closed(string body)
    {
        var source = Source(body);
        await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
            source.ResolveFreshAsync(UserId, TenantId, CancellationToken.None));
    }

    [Fact]
    public async Task Denial_with_grants_and_inactive_allow_fail_closed()
    {
        foreach (var body in new[]
        {
            $$"""{"tenantId":"{{TenantId}}","decision":"deny","permissions":["profile.app.access"]}""",
            $$"""{"tenantId":"{{TenantId}}","tenantStatus":"suspended","membershipStatus":"active","permissions":[]}""",
            $$"""{"tenantId":"{{TenantId}}","decision":"unknown","tenantStatus":"active","membershipStatus":"active","permissions":[]}"""
        })
        {
            await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
                Source(body).ResolveFreshAsync(UserId, TenantId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Non_success_resolve_response_fails_closed()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)));
        var source = new HttpAccessAuthzSource(new Factory(handler), new StaticToken(RandomValue()));
        await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
            source.ResolveFreshAsync(UserId, TenantId, CancellationToken.None));
    }

    [Fact]
    public async Task Token_request_uses_client_credentials_and_reuses_unexpired_token()
    {
        var clientId = "test-client";
        var secret = RandomValue();
        var token = RandomValue();
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://identity.example.test/realms/excellore/protocol/openid-connect/token",
                request.RequestUri!.ToString());
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!));
            Assert.Equal($"{clientId}:{secret}", decoded);
            Assert.Equal("grant_type=client_credentials", await request.Content!.ReadAsStringAsync());
            return Json($$"""{"access_token":"{{token}}","token_type":"Bearer","expires_in":120}""");
        });
        using var provider = new ClientCredentialsTokenProvider(new Factory(handler),
            new Uri("https://identity.example.test/realms/excellore/protocol/openid-connect/token"),
            clientId, secret, TimeProvider.System);

        Assert.Equal(token, await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(token, await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Invalid_token_responses_fail_closed_without_exposing_secret()
    {
        var secret = RandomValue();
        var token = RandomValue();
        foreach (var body in new[]
        {
            $$"""{"access_token":"{{token}}","token_type":"Bearer","expires_in":0}""",
            $$"""{"access_token":"{{token}}","token_type":"Mac","expires_in":120}""",
            """{"token_type":"Bearer","expires_in":120}"""
        })
        {
            var handler = new Handler(_ => Task.FromResult(Json(body)));
            using var provider = new ClientCredentialsTokenProvider(new Factory(handler),
                new Uri("https://identity.example.test/token"), "test-client", secret, TimeProvider.System);

            var error = await Assert.ThrowsAsync<AuthzUnavailableException>(() =>
                provider.GetTokenAsync(CancellationToken.None));
            Assert.DoesNotContain(secret, error.ToString());
            Assert.DoesNotContain(token, error.ToString());
            Assert.Equal(1, handler.Calls);
        }
    }

    [Fact]
    public void Registration_requires_https_for_token_endpoint_and_no_url_credentials()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Authz:AccessInternalUrl"] = "http://access.internal/",
            ["Authz:TokenEndpoint"] = "http://identity.internal/token",
            ["Authz:ClientId"] = "test-client",
            ["Authz:ClientSecret"] = RandomValue()
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAccessAuthzClient(configuration));
        new ServiceCollection().AddAccessAuthzClient(configuration,
            allowInsecureTokenEndpointForLocalDevelopment: true);

        settings["Authz:TokenEndpoint"] = "https://user:pass@identity.example.test/token";
        configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAccessAuthzClient(configuration));
    }

    private static HttpAccessAuthzSource Source(string body) => new(
        new Factory(new Handler(_ => Task.FromResult(Json(body)))), new StaticToken(RandomValue()));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string RandomValue() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private sealed class StaticToken(string token) : IAccessMachineTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://access.internal/")
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return await respond(request);
        }
    }
}
