using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Excellore.Authz;

public sealed record KeycloakDirectoryUser(Guid Id, bool Enabled, string? Email,
    string? FirstName, string? LastName, string? Locale);

public interface IKeycloakDirectory
{
    Task<IReadOnlyDictionary<Guid, KeycloakDirectoryUser>> ListAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Reads bounded Keycloak user pages without placing a person identifier in a URL.</summary>
public sealed class KeycloakDirectoryClient(IHttpClientFactory clients,
    ClientCredentialsTokenProvider tokens, Uri usersEndpoint) : IKeycloakDirectory
{
    public const string ClientName = "Excellore.Keycloak.Directory";
    private const int PageSize = 100;
    private const int MaxUsers = 10_000;
    private const long MaxPageBytes = 1024 * 1024;

    public async Task<IReadOnlyDictionary<Guid, KeycloakDirectoryUser>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var token = await tokens.GetTokenAsync(cancellationToken);
        var result = new Dictionary<Guid, KeycloakDirectoryUser>();
        using var client = clients.CreateClient(ClientName);
        for (var first = 0; first <= MaxUsers; first += PageSize)
        {
            var uri = new UriBuilder(usersEndpoint)
            {
                Query = $"first={first}&max={PageSize}"
            }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Identity directory unavailable.");
            await response.Content.LoadIntoBufferAsync(MaxPageBytes, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream,
                cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > PageSize)
                throw new InvalidOperationException("Identity directory response invalid.");
            foreach (var user in document.RootElement.EnumerateArray())
            {
                if (user.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Identity directory response invalid.");
                if (!user.TryGetProperty("id", out var idField) ||
                    idField.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(idField.GetString(), out var id) || id == Guid.Empty ||
                    !user.TryGetProperty("enabled", out var enabledField) ||
                    enabledField.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidOperationException("Identity directory response invalid.");
                var snapshot = new KeycloakDirectoryUser(id,
                    enabledField.GetBoolean(), ReadText(user, "email", 254),
                    ReadText(user, "firstName", 80), ReadText(user, "lastName", 80),
                    ReadLocale(user));
                if (!result.TryAdd(id, snapshot))
                    throw new InvalidOperationException("Identity directory response invalid.");
            }
            if (document.RootElement.GetArrayLength() < PageSize) return result;
        }
        throw new InvalidOperationException("Identity directory exceeds configured limit.");
    }

    public static (Uri Token, Uri Users) Endpoints(string? authority, bool allowLocalHttp,
        string? privateHttpHost = null)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps &&
             !(allowLocalHttp && parsed.Scheme == Uri.UriSchemeHttp &&
               parsed.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) &&
             !(parsed.Scheme == Uri.UriSchemeHttp && privateHttpHost is { Length: > 0 } &&
               string.Equals(parsed.Host, privateHttpHost, StringComparison.OrdinalIgnoreCase))) ||
            parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
            throw new ArgumentException("Identity authority is invalid.");
        var parts = parsed.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts[0] != "realms" || parts[1].Length == 0 ||
            !parts[1].All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_'))
            throw new ArgumentException("Identity authority is invalid.");
        var origin = parsed.GetLeftPart(UriPartial.Authority);
        return (new Uri($"{origin}/realms/{parts[1]}/protocol/openid-connect/token"),
            new Uri($"{origin}/admin/realms/{parts[1]}/users"));
    }

    private static string? ReadText(JsonElement user, string name, int maxLength)
    {
        if (!user.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null)
            return null;
        if (field.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Identity directory response invalid.");
        var value = field.GetString();
        return value is { Length: > 0 } && value.Length <= maxLength &&
            !value.Any(char.IsControl) ? value : null;
    }

    private static string? ReadLocale(JsonElement user)
    {
        if (!user.TryGetProperty("attributes", out var attributes) ||
            attributes.ValueKind != JsonValueKind.Object ||
            !attributes.TryGetProperty("locale", out var locale) ||
            locale.ValueKind != JsonValueKind.Array || locale.GetArrayLength() == 0)
            return null;
        var value = locale[0].ValueKind == JsonValueKind.String ? locale[0].GetString() : null;
        return value is { Length: > 0 and <= 35 } && !value.Any(char.IsControl)
            ? value : null;
    }
}

public static class KeycloakDirectoryRegistration
{
    public static IServiceCollection AddKeycloakDirectory(this IServiceCollection services,
        IConfiguration configuration, string clientId)
    {
        var allowLocalHttp = string.Equals(configuration["EXCELLORE_INSECURE_LOCAL_DEV"],
            "true", StringComparison.OrdinalIgnoreCase);
        var (token, users) = KeycloakDirectoryClient.Endpoints(
            configuration["IdentityDirectory:Authority"] ?? configuration["Auth:Authority"],
            allowLocalHttp, configuration["IdentityDirectory:PrivateHttpHost"]);
        var secret = configuration["IdentityDirectory:ClientSecret"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Identity directory credentials are missing.");
        services.AddHttpClient(ClientCredentialsTokenProvider.ClientName,
            client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient(KeycloakDirectoryClient.ClientName,
            client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton(provider => new ClientCredentialsTokenProvider(
            provider.GetRequiredService<IHttpClientFactory>(), token, clientId, secret,
            TimeProvider.System));
        services.AddSingleton(provider => new KeycloakDirectoryClient(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ClientCredentialsTokenProvider>(), users));
        services.AddSingleton<IKeycloakDirectory>(provider =>
            provider.GetRequiredService<KeycloakDirectoryClient>());
        return services;
    }
}
