using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Excellore.Profile.Application;
using Microsoft.Extensions.Configuration;

namespace Excellore.Profile.Infrastructure;

public sealed class KeycloakOwnIdentityClient(HttpClient http, IConfiguration configuration)
    : IOwnIdentityClient
{
    public async Task<bool> UpdateAsync(Guid userId, string? firstName, string? lastName,
        string? locale, CancellationToken cancellationToken)
    {
        if (firstName is null && lastName is null && locale is null) return true;
        if (userId == Guid.Empty) return false;
        var authority = configuration["Identity:Authority"] ?? configuration["Auth:Authority"];
        var secret = configuration["Identity:ClientSecret"];
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var issuer) ||
            issuer.AbsolutePath.TrimEnd('/') != "/realms/excellore" ||
            !Allowed(issuer) ||
            string.IsNullOrWhiteSpace(secret)) return false;
        var baseUri = issuer.GetLeftPart(UriPartial.Authority);
        var tokenUri = new Uri($"{baseUri}/realms/excellore/protocol/openid-connect/token");
        var userUri = new Uri($"{baseUri}/admin/realms/excellore/users/{userId:D}");
        try
        {
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = "svc-profile-identity",
                    ["client_secret"] = secret
                })
            };
            using var tokenResponse = await http.SendAsync(tokenRequest, cancellationToken);
            if (!tokenResponse.IsSuccessStatusCode) return false;
            using var tokenJson = await JsonDocument.ParseAsync(
                await tokenResponse.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (!tokenJson.RootElement.TryGetProperty("access_token", out var accessToken) ||
                accessToken.GetString() is not { Length: > 0 } token) return false;
            using var get = new HttpRequestMessage(HttpMethod.Get, userUri);
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var getResponse = await http.SendAsync(get, cancellationToken);
            if (!getResponse.IsSuccessStatusCode) return false;
            var current = await JsonNode.ParseAsync(
                await getResponse.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken) as JsonObject;
            if (current is null || current["id"]?.GetValue<string>() != userId.ToString("D"))
                return false;
            if (firstName is not null) current["firstName"] = firstName;
            if (lastName is not null) current["lastName"] = lastName;
            if (locale is not null)
            {
                var attributes = current["attributes"] as JsonObject;
                if (attributes is null)
                {
                    attributes = new JsonObject();
                    current["attributes"] = attributes;
                }
                attributes["locale"] = new JsonArray(locale);
            }
            using var put = new HttpRequestMessage(HttpMethod.Put, userUri)
            {
                Content = JsonContent.Create(current)
            };
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var putResponse = await http.SendAsync(put, cancellationToken);
            return putResponse.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or
            TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<OwnSession>?> GetSessionsAsync(Guid userId,
        CancellationToken cancellationToken)
    {
        var credentials = await CredentialsAsync(cancellationToken);
        if (credentials is null || userId == Guid.Empty) return null;
        var (issuer, token) = credentials.Value;
        var uri = new Uri($"{issuer.GetLeftPart(UriPartial.Authority)}/admin/realms/excellore/users/{userId:D}/sessions");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var sessions = new List<OwnSession>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("id", out var idElement) ||
                    !Guid.TryParse(idElement.GetString(), out var id)) continue;
                sessions.Add(new OwnSession(id, ReadEpoch(element, "start"),
                    ReadEpoch(element, "lastAccess")));
            }
            return sessions;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty) return false;
        var ownSessions = await GetSessionsAsync(userId, cancellationToken);
        if (ownSessions is null || !ownSessions.Any(item => item.Id == sessionId))
            return false;
        var credentials = await CredentialsAsync(cancellationToken);
        if (credentials is null) return false;
        var (issuer, token) = credentials.Value;
        var uri = new Uri($"{issuer.GetLeftPart(UriPartial.Authority)}/admin/realms/excellore/sessions/{sessionId:D}");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<(Uri Issuer, string Token)?> CredentialsAsync(CancellationToken ct)
    {
        var secret = configuration["Identity:ClientSecret"];
        if (!Uri.TryCreate(configuration["Identity:Authority"] ?? configuration["Auth:Authority"],
                UriKind.Absolute, out var issuer) ||
            issuer.AbsolutePath.TrimEnd('/') != "/realms/excellore" ||
            !Allowed(issuer) || string.IsNullOrWhiteSpace(secret))
            return null;
        try
        {
            var uri = new Uri($"{issuer.GetLeftPart(UriPartial.Authority)}/realms/excellore/protocol/openid-connect/token");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = "svc-profile-identity",
                    ["client_secret"] = secret
                })
            };
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return document.RootElement.TryGetProperty("access_token", out var value) &&
                value.GetString() is { Length: > 0 } token ? (issuer, token) : null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private bool Allowed(Uri authority)
    {
        if (authority.UserInfo.Length > 0 || authority.Query.Length > 0 ||
            authority.Fragment.Length > 0) return false;
        if (authority.Scheme == Uri.UriSchemeHttps) return true;
        if (authority.Scheme != Uri.UriSchemeHttp) return false;
        var local = string.Equals(configuration["EXCELLORE_INSECURE_LOCAL_DEV"], "true",
            StringComparison.OrdinalIgnoreCase) &&
            (authority.IsLoopback || authority.Host.EndsWith(".localhost",
                StringComparison.OrdinalIgnoreCase));
        var privateHost = configuration["Identity:PrivateHttpHost"];
        return local || privateHost is { Length: > 0 } &&
            string.Equals(authority.Host, privateHost, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? ReadEpoch(JsonElement element, string field)
    {
        if (!element.TryGetProperty(field, out var value) || !value.TryGetInt64(out var epoch))
            return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(epoch);
    }
}
