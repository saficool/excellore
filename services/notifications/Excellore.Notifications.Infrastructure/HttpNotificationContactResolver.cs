using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Excellore.Authz;
using Excellore.Notifications.Application;

namespace Excellore.Notifications.Infrastructure;

public sealed class HttpNotificationContactResolver(IHttpClientFactory clients,
    IAccessMachineTokenProvider tokens) : INotificationContactResolver
{
    private const long MaxResponseBytes = 16 * 1024;

    public async Task<NotificationContact?> ResolveAsync(Guid tenantId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
            throw new ArgumentException("A tenant and user are required.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "internal/v1/identities:resolve-notification-contact")
            {
                Content = JsonContent.Create(new { tenantId, userId })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await tokens.GetTokenAsync(cancellationToken));
            using var client = clients.CreateClient(HttpAccessAuthzSource.ClientName);
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Notification contact resolution failed.");
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream,
                cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("email", out var email) ||
                email.ValueKind != JsonValueKind.String ||
                email.GetString() is not { Length: > 0 and <= 254 } address)
                throw new InvalidOperationException("Notification contact resolution failed.");
            return new NotificationContact(address);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException)
        {
            throw new InvalidOperationException("Notification contact resolution failed.");
        }
    }
}
