using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Excellore.Access.Infrastructure;

public sealed record IdentityAdminActor(Guid Sub, string? Acr);
public sealed record IdentityAdminOptions(string? Authority, string? ClientSecret,
    string? AcceptanceOrigin, bool AllowLocalHttp, string? PrivateHttpHost = null);
public enum IdentityCreationStatus { Created, Existing, Unavailable }
public sealed record IdentityCreationResult(IdentityCreationStatus Status, Guid? UserId = null);

public interface IIdentityAdmin
{
    Task<IdentityCreationResult> EnsureInvitationIdentityAsync(string email, Guid invitationId,
        IdentityAdminActor actor, CancellationToken cancellationToken = default);
    Task<bool> SendRequiredActionsAsync(Guid userId, Guid invitationId,
        IdentityAdminActor actor, CancellationToken cancellationToken = default);
}

public sealed class IdentityAdminCircuit
{
    private readonly object gate = new();
    private int failures;
    private DateTimeOffset openUntil;

    public bool CanAttempt()
    {
        lock (gate) return DateTimeOffset.UtcNow >= openUntil;
    }

    public void Succeeded()
    {
        lock (gate) { failures = 0; openUntil = default; }
    }

    public void Failed()
    {
        lock (gate)
        {
            failures++;
            if (failures >= 3) openUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        }
    }
}

public sealed class KeycloakIdentityAdmin(HttpClient http, IdentityAdminOptions options,
    IdentityAdminCircuit circuit) : IIdentityAdmin
{
    private const string ClientId = "svc-access-admin";

    public async Task<IdentityCreationResult> EnsureInvitationIdentityAsync(string email,
        Guid invitationId, IdentityAdminActor actor,
        CancellationToken cancellationToken = default)
    {
        if (actor.Sub == Guid.Empty || invitationId == Guid.Empty || !TryUris(out var tokenUri,
                out var usersUri, out _) || !circuit.CanAttempt())
            return new(IdentityCreationStatus.Unavailable);
        var token = await GetTokenAsync(tokenUri!, cancellationToken);
        if (token is null) return new(IdentityCreationStatus.Unavailable);
        var username = $"invited-{invitationId:N}";
        try
        {
            using var create = new HttpRequestMessage(HttpMethod.Post, usersUri)
            {
                Content = JsonContent.Create(new
                {
                    username, email, enabled = true, emailVerified = false,
                    requiredActions = new[] { "VERIFY_EMAIL", "UPDATE_PASSWORD", "CONFIGURE_TOTP" }
                })
            };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(create, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Created &&
                Guid.TryParse(response.Headers.Location?.Segments.LastOrDefault(), out var createdId))
            {
                circuit.Succeeded();
                return new(IdentityCreationStatus.Created, createdId);
            }
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // Recover our own user after a lost create response without putting email in a URL.
                var recovered = await FindOurUserAsync(usersUri!, username, email, token,
                    cancellationToken);
                if (!recovered.Success)
                {
                    circuit.Failed();
                    return new(IdentityCreationStatus.Unavailable);
                }
                circuit.Succeeded();
                return recovered.UserId is Guid id
                    ? new(IdentityCreationStatus.Created, id)
                    : new(IdentityCreationStatus.Existing);
            }
            circuit.Failed();
            return new(IdentityCreationStatus.Unavailable);
        }
        catch (HttpRequestException)
        {
            circuit.Failed();
            return new(IdentityCreationStatus.Unavailable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            circuit.Failed();
            return new(IdentityCreationStatus.Unavailable);
        }
        catch (JsonException)
        {
            circuit.Failed();
            return new(IdentityCreationStatus.Unavailable);
        }
    }

    public async Task<bool> SendRequiredActionsAsync(Guid userId, Guid invitationId,
        IdentityAdminActor actor, CancellationToken cancellationToken = default)
    {
        if (actor.Sub == Guid.Empty || userId == Guid.Empty || invitationId == Guid.Empty ||
            !TryUris(out var tokenUri, out var usersUri, out var origin) || !circuit.CanAttempt())
            return false;
        var token = await GetTokenAsync(tokenUri!, cancellationToken);
        if (token is null) return false;
        var redirect = new Uri(origin!, "/invitations/accept");
        var target = new Uri(usersUri!.ToString().TrimEnd('/') + $"/{userId:D}/execute-actions-email" +
            $"?client_id=excellore-access&redirect_uri={Uri.EscapeDataString(redirect.ToString())}&lifespan=86400");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, target)
            {
                Content = JsonContent.Create(new[] { "VERIFY_EMAIL", "UPDATE_PASSWORD", "CONFIGURE_TOTP" })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) { circuit.Succeeded(); return true; }
            circuit.Failed();
            return false;
        }
        catch (HttpRequestException)
        {
            circuit.Failed();
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            circuit.Failed();
            return false;
        }
    }

    private async Task<(bool Success, Guid? UserId)> FindOurUserAsync(Uri usersUri,
        string username, string email,
        string token, CancellationToken cancellationToken)
    {
        var target = new Uri($"{usersUri}?username={Uri.EscapeDataString(username)}&exact=true");
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return (false, null);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return (false, null);
        foreach (var item in document.RootElement.EnumerateArray())
            if (item.TryGetProperty("username", out var actualUsername) &&
                actualUsername.GetString() == username &&
                item.TryGetProperty("email", out var actualEmail) &&
                string.Equals(actualEmail.GetString(), email, StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var userId))
                return (true, userId);
        return (true, null);
    }

    private async Task<string?> GetTokenAsync(Uri tokenUri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials", ["client_id"] = ClientId,
                        ["client_secret"] = options.ClientSecret!
                    })
                };
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var document = await JsonDocument.ParseAsync(
                        await response.Content.ReadAsStreamAsync(cancellationToken),
                        cancellationToken: cancellationToken);
                    if (document.RootElement.TryGetProperty("access_token", out var value))
                    {
                        var token = value.GetString();
                        if (!string.IsNullOrEmpty(token)) return token;
                    }
                }
                if ((int)response.StatusCode < 500) break;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (JsonException) { break; }
            if (attempt == 0) await Task.Delay(150, cancellationToken);
        }
        circuit.Failed();
        return null;
    }

    private bool TryUris(out Uri? tokenUri, out Uri? usersUri, out Uri? origin)
    {
        tokenUri = null;
        usersUri = null;
        origin = null;
        if (options.ClientSecret is not { Length: > 0 } ||
            !Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) ||
            !Uri.TryCreate(options.AcceptanceOrigin, UriKind.Absolute, out origin) ||
            authority.AbsolutePath.TrimEnd('/') != "/realms/excellore" ||
            !Allowed(authority, options.PrivateHttpHost) || !Allowed(origin, null) ||
            origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            authority.Query.Length != 0 || authority.Fragment.Length != 0)
            return false;
        tokenUri = new Uri(authority.ToString().TrimEnd('/') + "/protocol/openid-connect/token");
        usersUri = new Uri(authority.GetLeftPart(UriPartial.Authority) +
            "/admin/realms/excellore/users");
        return true;
    }

    private bool Allowed(Uri uri, string? privateHttpHost) => uri.Scheme == Uri.UriSchemeHttps ||
        options.AllowLocalHttp && uri.Scheme == Uri.UriSchemeHttp &&
        (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) ||
        uri.Scheme == Uri.UriSchemeHttp && privateHttpHost is { Length: > 0 } &&
        string.Equals(uri.Host, privateHttpHost, StringComparison.OrdinalIgnoreCase);
}
