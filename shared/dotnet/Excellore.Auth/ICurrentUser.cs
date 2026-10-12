namespace Excellore.Auth;

public interface ICurrentUser
{
    Guid UserId { get; }
    string? Email { get; }
    string? Acr { get; }
    bool HasRecentMfa { get; }
}
