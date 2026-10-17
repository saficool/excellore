using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Excellore.Auth;

public static class ExcelloreAuthentication
{
    public const string HighAssuranceAcr = "2";

    public static IServiceCollection AddExcelloreAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        string audience,
        bool requireHttpsMetadata = true)
    {
        var authority = configuration["Auth:Authority"];
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var issuer) ||
            (issuer.Scheme != Uri.UriSchemeHttps &&
             !(issuer.Scheme == Uri.UriSchemeHttp && !requireHttpsMetadata)) ||
            !issuer.AbsolutePath.TrimEnd('/').EndsWith("/realms/excellore", StringComparison.Ordinal))
        {
            throw new ArgumentException("Auth:Authority must be the excellore realm issuer URL.", nameof(configuration));
        }

        if (string.IsNullOrWhiteSpace(audience) || !audience.StartsWith("api-", StringComparison.Ordinal))
        {
            throw new ArgumentException("A single API audience is required.", nameof(audience));
        }

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = issuer.AbsoluteUri.TrimEnd('/');
                options.Audience = audience;
                options.RequireHttpsMetadata = requireHttpsMetadata;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles"
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("Internal.Authz", policy => policy.RequireAuthenticatedUser()
                .RequireRole("access.authz.resolve")
                .RequireAssertion(context => context.User.FindFirst("azp")?.Value is { } client &&
                    client.StartsWith("svc-", StringComparison.Ordinal) &&
                    client.EndsWith("-authz", StringComparison.Ordinal)));
            options.AddPolicy("Internal.Catalog", policy => policy.RequireAuthenticatedUser()
                .RequireRole("access.catalog.sync")
                .RequireClaim("azp", "svc-catalog-sync"));
            options.AddPolicy("Internal.Notifications", policy => policy.RequireAuthenticatedUser()
                .RequireRole("access.notification.contact.resolve")
                .RequireClaim("azp", "svc-notifications-authz"));
            options.AddPolicy("Platform.Operator", policy => policy.RequireAuthenticatedUser().RequireClaim("platform_roles", "platform-operator", "platform-admin"));
            options.AddPolicy("Platform.Staff", policy => policy.RequireAuthenticatedUser().RequireClaim("platform_roles", "platform-operator", "platform-support", "platform-auditor", "platform-security", "platform-admin"));
            options.AddPolicy("Platform.Suspend", policy => policy.RequireAuthenticatedUser().RequireClaim("platform_roles", "platform-operator", "platform-security", "platform-admin"));
            options.AddPolicy("Platform.Support", policy => policy.RequireAuthenticatedUser().RequireClaim("platform_roles", "platform-support", "platform-admin"));
            options.AddPolicy("Platform.Auditor", policy => policy.RequireAuthenticatedUser().RequireClaim("platform_roles", "platform-auditor", "platform-admin"));
            options.AddPolicy("Platform.Security", policy => policy.RequireAuthenticatedUser().RequireClaim("platform_roles", "platform-security", "platform-admin"));
        });

        return services;
    }
}
