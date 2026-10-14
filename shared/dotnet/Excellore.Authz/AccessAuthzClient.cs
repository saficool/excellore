using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Excellore.Authz;

public interface IAccessMachineTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed class HttpAccessAuthzSource : IAuthzSnapshotSource
{
    public const string ClientName = "Excellore.Access.Authz";
    private const long MaxResponseBytes = 1024 * 1024;
    private readonly IHttpClientFactory _clients;
    private readonly IAccessMachineTokenProvider _tokens;

    public HttpAccessAuthzSource(IHttpClientFactory clients, IAccessMachineTokenProvider tokens)
    {
        _clients = clients;
        _tokens = tokens;
    }

    public async Task<AuthzSnapshot> ResolveFreshAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("A user and tenant are required to resolve authorization.");
        }

        try
        {
            var token = await _tokens.GetTokenAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, "internal/v1/authz/resolve")
            {
                Content = JsonContent.Create(new { userId, tenantId })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var client = _clients.CreateClient(ClientName);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new AuthzUnavailableException();
            }

            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGuid(root, "tenantId", out var actualTenant) || actualTenant != tenantId ||
                !root.TryGetProperty("permissions", out var permissionsElement) ||
                permissionsElement.ValueKind != JsonValueKind.Array)
            {
                throw new AuthzUnavailableException();
            }

            var permissions = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var item in permissionsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    throw new AuthzUnavailableException();
                }

                permissions.Add(item.GetString()!);
            }

            string? decision = null;
            if (root.TryGetProperty("decision", out var decisionElement))
            {
                if (decisionElement.ValueKind != JsonValueKind.String)
                {
                    throw new AuthzUnavailableException();
                }

                decision = decisionElement.GetString();
            }

            if (decision == "deny" && permissions.Count == 0)
            {
                return new AuthzSnapshot(tenantId, AuthzDecision.Deny, null, null, permissions.ToImmutable());
            }

            if (decision is not null and not "allow" ||
                !TryString(root, "tenantStatus", out var tenantStatus) || tenantStatus != "active" ||
                !TryString(root, "membershipStatus", out var membershipStatus) || membershipStatus != "active")
            {
                throw new AuthzUnavailableException();
            }

            return new AuthzSnapshot(tenantId, AuthzDecision.Allow, tenantStatus, membershipStatus,
                permissions.ToImmutable());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException)
        {
            throw new AuthzUnavailableException();
        }
    }

    private static bool TryGuid(JsonElement root, string name, out Guid value)
    {
        value = default;
        return root.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.String && element.TryGetGuid(out value);
    }

    private static bool TryString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }
}

public sealed class ClientCredentialsTokenProvider : IAccessMachineTokenProvider, IDisposable
{
    public const string ClientName = "Excellore.Access.Token";
    private const long MaxResponseBytes = 64 * 1024;
    private readonly IHttpClientFactory _clients;
    private readonly Uri _tokenEndpoint;
    private readonly string _authorization;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private string? _token;
    private DateTimeOffset _refreshAt;

    public ClientCredentialsTokenProvider(IHttpClientFactory clients, Uri tokenEndpoint,
        string clientId, string clientSecret, TimeProvider clock)
    {
        _clients = clients;
        _tokenEndpoint = tokenEndpoint;
        _authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(clientSecret)}"));
        _clock = clock;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && _clock.GetUtcNow() < _refreshAt)
            {
                return _token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials"
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authorization);

            try
            {
                using var client = _clients.CreateClient(ClientName);
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new AuthzUnavailableException();
                }

                await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("access_token", out var tokenElement) ||
                    tokenElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(tokenElement.GetString()) ||
                    !root.TryGetProperty("token_type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String ||
                    !string.Equals(typeElement.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("expires_in", out var expiryElement) ||
                    !expiryElement.TryGetInt32(out var expiresIn) || expiresIn <= 0)
                {
                    throw new AuthzUnavailableException();
                }

                _token = tokenElement.GetString();
                _refreshAt = _clock.GetUtcNow().AddSeconds(Math.Max(0, expiresIn - 60));
                return _token!;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException)
            {
                throw new AuthzUnavailableException();
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public void Dispose() => _sync.Dispose();
}

public static class AccessAuthzClientRegistration
{
    public static IServiceCollection AddAccessAuthzClient(this IServiceCollection services,
        IConfiguration configuration, bool allowInsecureTokenEndpointForLocalDevelopment = false)
    {
        var accessUrl = RequiredUri(configuration["Authz:AccessInternalUrl"], "AccessInternalUrl", allowHttp: true);
        if (accessUrl.AbsolutePath != "/")
        {
            throw new ArgumentException("Authz:AccessInternalUrl must be an origin URL without a path.");
        }
        var tokenUrl = RequiredUri(configuration["Authz:TokenEndpoint"], "TokenEndpoint",
            allowHttp: allowInsecureTokenEndpointForLocalDevelopment);
        var clientId = RequiredValue(configuration["Authz:ClientId"], "ClientId");
        var clientSecret = RequiredValue(configuration["Authz:ClientSecret"], "ClientSecret");

        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(HttpAccessAuthzSource.ClientName, client =>
        {
            client.BaseAddress = accessUrl;
            client.Timeout = TimeSpan.FromSeconds(5);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient(ClientCredentialsTokenProvider.ClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.TryAddSingleton<IAccessMachineTokenProvider>(provider => new ClientCredentialsTokenProvider(
            provider.GetRequiredService<IHttpClientFactory>(), tokenUrl, clientId, clientSecret,
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<HttpAccessAuthzSource>();
        services.TryAddSingleton<IAuthzSnapshotSource>(provider =>
            provider.GetRequiredService<HttpAccessAuthzSource>());
        return services;
    }

    private static string RequiredValue(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Authz:{name} is required.")
            : value;

    private static Uri RequiredUri(string? value, string name, bool allowHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException($"Authz:{name} must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        }

        return uri;
    }
}
