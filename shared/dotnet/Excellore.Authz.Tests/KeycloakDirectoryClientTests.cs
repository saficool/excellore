using System.Net;
using System.Text;
using System.Text.Json;
using Excellore.Authz;

namespace Excellore.Authz.Tests;

public sealed class KeycloakDirectoryClientTests
{
    [Fact]
    public async Task Directory_reader_pages_without_personal_data_in_urls()
    {
        var userId = Guid.NewGuid();
        var requested = new List<Uri>();
        using var handler = new DelegateHandler(request =>
        {
            requested.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                return Json("{\"access_token\":\"test-token\",\"token_type\":\"Bearer\",\"expires_in\":300}");
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            return Json(JsonSerializer.Serialize(new[] { new
            {
                id = userId, enabled = true, email = "private@example.invalid",
                firstName = "Ada", lastName = "Example", attributes = new { locale = new[] { "en" } }
            } }));
        });
        var factory = new Factory(handler);
        var (token, users) = KeycloakDirectoryClient.Endpoints(
            "http://auth.excellore.localhost:8080/realms/excellore", true);
        using var credentials = new ClientCredentialsTokenProvider(factory, token,
            "worker-profile", "test-secret", TimeProvider.System);
        var directory = new KeycloakDirectoryClient(factory, credentials, users);

        var result = await directory.ListAsync();

        Assert.Single(result);
        Assert.Equal("Ada", result[userId].FirstName);
        Assert.Equal("en", result[userId].Locale);
        Assert.DoesNotContain(requested, uri => uri.ToString().Contains(userId.ToString("D"),
            StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requested, uri => uri.ToString().Contains("private@",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requested, uri => uri.Query == "?first=0&max=100");
    }

    [Fact]
    public void Directory_endpoint_rejects_nonlocal_http_and_embedded_credentials()
    {
        Assert.Throws<ArgumentException>(() => KeycloakDirectoryClient.Endpoints(
            "http://identity.example/realms/excellore", true));
        Assert.Throws<ArgumentException>(() => KeycloakDirectoryClient.Endpoints(
            "https://name:secret@identity.example/realms/excellore", false));
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
}
