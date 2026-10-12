using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Excellore.Auth;

public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal Principal
    {
        get
        {
            var principal = httpContextAccessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                throw new InvalidOperationException("No authenticated user is available.");
            }

            return principal;
        }
    }

    public Guid UserId => Guid.TryParse(Principal.FindFirstValue("sub"), out var userId)
        ? userId
        : throw new InvalidOperationException("The authenticated subject is missing or invalid.");

    public string? Email => Principal.FindFirstValue("email");

    public string? Acr => Principal.FindFirstValue("acr");

    public bool HasRecentMfa => Acr == ExcelloreAuthentication.HighAssuranceAcr;
}
