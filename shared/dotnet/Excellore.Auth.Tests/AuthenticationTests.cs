using System.Security.Claims;
using System.Security.Cryptography;
using Excellore.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Excellore.Auth.Tests;

public class AuthenticationTests
{
    private const string LocalAuthority = "http://auth.excellore.localhost:8080/realms/excellore";

    [Fact]
    public void Jwt_validation_is_scoped_to_one_api_and_preserves_keycloak_claims()
    {
        using var provider = BuildServices("api-profile");
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Bearer");

        Assert.Equal(LocalAuthority, options.Authority);
        Assert.Equal("api-profile", options.Audience);
        Assert.False(options.RequireHttpsMetadata);
        Assert.False(options.MapInboundClaims);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.ValidateLifetime);
        Assert.True(options.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.Equal("api-profile", options.TokenValidationParameters.ValidAudience);
        Assert.Equal(TimeSpan.FromSeconds(30), options.TokenValidationParameters.ClockSkew);
        Assert.Equal("roles", options.TokenValidationParameters.RoleClaimType);
    }

    [Fact]
    public async Task Profile_validation_rejects_an_access_api_token()
    {
        using var provider = BuildServices("api-profile");
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Bearer");
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var handler = new JsonWebTokenHandler();
        var validation = options.TokenValidationParameters.Clone();
        validation.ValidIssuer = LocalAuthority;
        validation.IssuerSigningKey = key;

        string TokenFor(string audience) => handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalAuthority,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())])
        });
        var accepted = await handler.ValidateTokenAsync(TokenFor("api-profile"), validation);
        var rejected = await handler.ValidateTokenAsync(TokenFor("api-access"), validation);

        Assert.True(accepted.IsValid);
        Assert.False(rejected.IsValid);
        Assert.IsType<SecurityTokenInvalidAudienceException>(rejected.Exception);
    }

    [Theory]
    [InlineData("http://auth.excellore.localhost:8080/realms/excellore")]
    [InlineData("https://auth.example.test/realms/other")]
    [InlineData("not-a-url")]
    public void Production_configuration_rejects_non_https_or_wrong_realm(string authority)
    {
        var services = new ServiceCollection();
        var configuration = Configuration(authority);
        Assert.Throws<ArgumentException>(() =>
            services.AddExcelloreAuthentication(configuration, "api-profile"));
    }

    [Fact]
    public void Current_user_reads_subject_and_requires_high_acr_for_step_up()
    {
        var id = Guid.NewGuid();
        var context = new DefaultHttpContext();
        var currentUser = new CurrentUser(new HttpContextAccessor { HttpContext = context });
        context.User = Principal(new Claim("sub", id.ToString()), new Claim("email", "person@example.test"), new Claim("acr", "1"));

        Assert.Equal(id, currentUser.UserId);
        Assert.Equal("person@example.test", currentUser.Email);
        Assert.Equal("1", currentUser.Acr);
        Assert.False(currentUser.HasRecentMfa);

        context.User = Principal(new Claim("sub", id.ToString()), new Claim("acr", "2"));
        Assert.True(currentUser.HasRecentMfa);
    }

    [Fact]
    public void Missing_or_invalid_subject_fails_closed()
    {
        var context = new DefaultHttpContext { User = Principal(new Claim("sub", "not-a-guid")) };
        var currentUser = new CurrentUser(new HttpContextAccessor { HttpContext = context });

        Assert.Throws<InvalidOperationException>(() => currentUser.UserId);
        context.User = Principal();
        Assert.Throws<InvalidOperationException>(() => currentUser.UserId);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", Guid.NewGuid().ToString()), new Claim("acr", "2")]));
        Assert.Throws<InvalidOperationException>(() => currentUser.HasRecentMfa);
    }

    [Theory]
    [InlineData("Internal.Authz", "roles", "access.authz.resolve", false)]
    [InlineData("Internal.Authz", "platform_roles", "platform-admin", false)]
    [InlineData("Internal.Catalog", "roles", "access.catalog.sync", false)]
    [InlineData("Platform.Operator", "platform_roles", "platform-operator", true)]
    [InlineData("Platform.Operator", "roles", "platform-operator", false)]
    [InlineData("Platform.Security", "platform_roles", "platform-admin", true)]
    public async Task Machine_and_platform_policies_use_separate_claims(
        string policyName, string claimType, string claimValue, bool expected)
    {
        using var provider = BuildServices("api-access");
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(Principal(new Claim(claimType, claimValue)), policyName);

        Assert.Equal(expected, result.Succeeded);
    }

    [Theory]
    [InlineData("Internal.Authz", "access.authz.resolve", "svc-shell-authz", true)]
    [InlineData("Internal.Authz", "access.authz.resolve", "svc-profile-authz", true)]
    [InlineData("Internal.Authz", "access.authz.resolve", "excellore-access", false)]
    [InlineData("Internal.Authz", "access.catalog.sync", "svc-shell-authz", false)]
    [InlineData("Internal.Catalog", "access.catalog.sync", "svc-catalog-sync", true)]
    [InlineData("Internal.Catalog", "access.catalog.sync", "excellore-access", false)]
    [InlineData("Internal.Catalog", "access.catalog.sync", "svc-shell-authz", false)]
    public async Task Internal_policies_require_the_expected_machine_client_and_role(
        string policyName, string role, string client, bool expected)
    {
        using var provider = BuildServices("api-access");
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var principal = Principal(new Claim("roles", role), new Claim("azp", client));

        Assert.Equal(expected, (await authorization.AuthorizeAsync(principal, policyName)).Succeeded);
    }

    private static ServiceProvider BuildServices(string audience)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExcelloreAuthentication(Configuration(LocalAuthority), audience, false);
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(string authority) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = authority
        }).Build();

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Bearer", "preferred_username", "roles"));
}
