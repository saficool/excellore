using System.Collections.Immutable;
using System.Security.Claims;
using Excellore.Auth;
using Excellore.Authz;
using Excellore.Sample.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Excellore.Sample.Tests;

public sealed class SamplePermissionTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task SampleReadPermissionReachesHandlerOnlyWhenGranted(bool granted, bool expectedCall)
    {
        var tenant = Guid.NewGuid();
        var permissions = granted
            ? ImmutableHashSet.Create(StringComparer.Ordinal, "__APP_SLUG__.item.read")
            : ImmutableHashSet<string>.Empty;
        var snapshot = new AuthzSnapshot(tenant, AuthzDecision.Allow, "active", "active", permissions);
        var filter = new TenantGateFilter(new StubResolver(snapshot), new StubUser(),
            new TenantContextAccessor(), new SamplePrivilegeClassifier(),
            NullLogger<TenantGateFilter>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "Bearer"))
        };
        http.Request.RouteValues["tenantId"] = tenant.ToString("D");
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new RequirePermissionMetadata(
                ImmutableArray.Create("__APP_SLUG__.item.read"))), "sample-read"));
        var called = false;

        var result = await filter.InvokeAsync(EndpointFilterInvocationContext.Create(http), _ =>
        {
            called = true;
            return ValueTask.FromResult<object?>("ok");
        });

        Assert.Equal(expectedCall, called);
        if (granted) Assert.Equal("ok", result);
        else Assert.Equal(403, Assert.IsType<ProblemHttpResult>(result).StatusCode);
    }

    [Fact]
    public void UnknownPermissionRequiresStepUp() =>
        Assert.True(new SamplePrivilegeClassifier().IsPrivileged("__APP_SLUG__.future.manage"));

    private sealed class StubResolver(AuthzSnapshot snapshot) : IAuthzResolver
    {
        public Task<AuthzSnapshot> ResolveAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }

    private sealed class StubUser : ICurrentUser
    {
        public Guid UserId => Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        public string? Email => null;
        public string? Acr => "2";
        public bool HasRecentMfa => true;
    }
}
