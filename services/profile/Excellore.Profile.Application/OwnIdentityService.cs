using Excellore.Auth;

namespace Excellore.Profile.Application;

public interface IOwnIdentityClient
{
    Task<bool> UpdateAsync(Guid userId, string? firstName, string? lastName,
        string? locale, CancellationToken cancellationToken);
    Task<IReadOnlyList<OwnSession>?> GetSessionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
}

public sealed record OwnSession(Guid Id, DateTimeOffset? StartedAt, DateTimeOffset? LastActiveAt);

// The API body contains no target user identifier. Only the validated token sub
// can select an identity; callers cannot turn this into an update-other-user API.
public sealed class OwnIdentityService(ICurrentUser currentUser, IOwnIdentityClient client)
{
    public Task<bool> UpdateAsync(UpdateOwnProfile input, CancellationToken cancellationToken = default) =>
        client.UpdateAsync(currentUser.UserId, input.FirstName, input.LastName,
            input.Locale, cancellationToken);
    public Task<IReadOnlyList<OwnSession>?> GetSessionsAsync(CancellationToken cancellationToken = default) =>
        client.GetSessionsAsync(currentUser.UserId, cancellationToken);
    public Task<bool> RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        client.RevokeSessionAsync(currentUser.UserId, sessionId, cancellationToken);
}
