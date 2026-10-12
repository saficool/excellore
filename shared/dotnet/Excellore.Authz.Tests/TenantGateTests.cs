using System.Collections.Immutable;
using System.Security.Claims;
using Excellore.Auth;
using Excellore.Authz;
using Excellore.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Excellore.Authz.Tests;

public class TenantGateTests
{
    private static readonly Guid TenantOne = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid TenantTwo = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    [Fact]
    public void Database_tenant_accessor_uses_the_same_scoped_gate_context()
    {
        using var provider = new ServiceCollection().AddExcelloreAuthz().BuildServiceProvider();
        using var scope = provider.CreateScope();
        var gateContext = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        var databaseContext = scope.ServiceProvider.GetRequiredService<ITenantIdAccessor>();
        Assert.Same(gateContext, databaseContext);
        Assert.Throws<InvalidOperationException>(() => databaseContext.TenantId);

        gateContext.Set(TenantOne, Allowed(TenantOne));
        Assert.Equal(TenantOne, databaseContext.TenantId);
        gateContext.Clear();
        Assert.Throws<InvalidOperationException>(() => databaseContext.TenantId);
    }

    [Fact]
    public async Task Allowed_member_reaches_handler_with_tenant_context_then_clears_it()
    {
        var resolver = new StubResolver(Allowed(TenantOne, "profile.member.read"));
        var tenantContext = new TenantContextAccessor();
        var filter = new TenantGateFilter(resolver, new StubUser(true), tenantContext, new StubClassifier(false));
        var invocation = Invocation(TenantOne);
        var called = false;

        var result = await filter.InvokeAsync(invocation, _ =>
        {
            called = true;
            Assert.Equal(TenantOne, tenantContext.Current?.TenantId);
            return ValueTask.FromResult<object?>("ok");
        });

        Assert.True(called);
        Assert.Equal("ok", result);
        Assert.Null(tenantContext.Current);
        Assert.Equal(UserId, resolver.LastUserId);
        Assert.Equal(TenantOne, resolver.LastTenantId);
    }

    [Fact]
    public async Task Handler_failure_does_not_leave_tenant_context_open()
    {
        var tenantContext = new TenantContextAccessor();
        var filter = new TenantGateFilter(
            new StubResolver(Allowed(TenantOne, "profile.member.read")),
            new StubUser(true), tenantContext, new StubClassifier(false));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await filter.InvokeAsync(Invocation(TenantOne), _ =>
                throw new InvalidOperationException("handler failed")));

        Assert.Null(tenantContext.Current);
    }

    [Theory]
    [InlineData("non-member", 404, "not-found")]
    [InlineData("suspended", 404, "not-found")]
    [InlineData("missing-permission", 403, "forbidden")]
    [InlineData("wrong-tenant", 404, "not-found")]
    [InlineData("step-up", 403, "step-up-required")]
    public async Task Denied_decisions_never_reach_handler(string scenario, int status, string problem)
    {
        var snapshot = scenario switch
        {
            "non-member" => new AuthzSnapshot(TenantOne, AuthzDecision.Deny, "active", null, EmptyPermissions()),
            "suspended" => new AuthzSnapshot(TenantOne, AuthzDecision.Allow, "suspended", "active", Permissions("profile.member.read")),
            "missing-permission" => Allowed(TenantOne),
            "wrong-tenant" => Allowed(TenantTwo, "profile.member.read"),
            _ => Allowed(TenantOne, "profile.member.read")
        };
        var resolver = new StubResolver(snapshot);
        var tenantContext = new TenantContextAccessor();
        var filter = new TenantGateFilter(
            resolver, new StubUser(false), tenantContext, new StubClassifier(scenario == "step-up"));
        var called = false;

        var result = await filter.InvokeAsync(Invocation(TenantOne), _ =>
        {
            called = true;
            return ValueTask.FromResult<object?>("leaked");
        });

        Assert.False(called);
        Assert.Null(tenantContext.Current);
        AssertProblem(result, status, problem);
    }

    [Fact]
    public async Task Privileged_permission_succeeds_after_mfa()
    {
        var filter = new TenantGateFilter(
            new StubResolver(Allowed(TenantOne, "profile.member.read")),
            new StubUser(true),
            new TenantContextAccessor(),
            new StubClassifier(true));

        var result = await filter.InvokeAsync(Invocation(TenantOne), _ => ValueTask.FromResult<object?>("ok"));

        Assert.Equal("ok", result);
    }

    [Fact]
    public void Unknown_permissions_require_step_up_by_default()
    {
        Assert.True(new ConservativePrivilegeClassifier().IsPrivileged("profile.future.action"));
    }

    [Fact]
    public async Task Unavailable_authority_fails_closed()
    {
        var resolver = new StubResolver(Allowed(TenantOne)) { Unavailable = true };
        var filter = new TenantGateFilter(resolver, new StubUser(true), new TenantContextAccessor(), new StubClassifier(false));
        var called = false;

        var result = await filter.InvokeAsync(Invocation(TenantOne), _ =>
        {
            called = true;
            return ValueTask.FromResult<object?>("leaked");
        });

        Assert.False(called);
        AssertProblem(result, 503, "authz-unavailable");
    }

    [Fact]
    public async Task Missing_permission_metadata_fails_before_resolving()
    {
        var resolver = new StubResolver(Allowed(TenantOne, "profile.member.read"));
        var filter = new TenantGateFilter(resolver, new StubUser(true), new TenantContextAccessor(), new StubClassifier(false));

        var result = await filter.InvokeAsync(Invocation(TenantOne, permission: null), _ =>
            ValueTask.FromResult<object?>("leaked"));

        Assert.Null(resolver.LastTenantId);
        AssertProblem(result, 403, "forbidden");
    }

    [Fact]
    public async Task Invalid_route_tenant_does_not_call_resolver()
    {
        var resolver = new StubResolver(Allowed(TenantOne, "profile.member.read"));
        var filter = new TenantGateFilter(resolver, new StubUser(true), new TenantContextAccessor(), new StubClassifier(false));
        var invocation = Invocation(TenantOne);
        invocation.HttpContext.Request.RouteValues["tenantId"] = "not-a-guid";

        var result = await filter.InvokeAsync(invocation, _ => ValueTask.FromResult<object?>("leaked"));

        Assert.Null(resolver.LastTenantId);
        AssertProblem(result, 404, "not-found");
    }

    private static EndpointFilterInvocationContext Invocation(Guid tenantId, string? permission = "profile.member.read")
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", UserId.ToString())], "Bearer"))
        };
        http.Request.RouteValues["tenantId"] = tenantId.ToString();
        var metadata = permission is null
            ? new EndpointMetadataCollection()
            : new EndpointMetadataCollection(new RequirePermissionMetadata(ImmutableArray.Create(permission)));
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "tenant-test"));
        return EndpointFilterInvocationContext.Create(http);
    }

    private static ImmutableHashSet<string> Permissions(params string[] permissions) =>
        ImmutableHashSet.CreateRange(StringComparer.Ordinal, permissions);
    private static ImmutableHashSet<string> EmptyPermissions() => Permissions();
    private static AuthzSnapshot Allowed(Guid tenantId, params string[] permissions) =>
        new(tenantId, AuthzDecision.Allow, "active", "active", Permissions(permissions));

    private static void AssertProblem(object? result, int status, string problem)
    {
        var actual = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(status, actual.StatusCode);
        Assert.Equal($"urn:excellore:problem:{problem}", actual.ProblemDetails.Type);
    }

    private sealed class StubResolver(AuthzSnapshot snapshot) : IAuthzResolver
    {
        public Guid? LastUserId { get; private set; }
        public Guid? LastTenantId { get; private set; }
        public bool Unavailable { get; init; }

        public Task<AuthzSnapshot> ResolveAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
        {
            LastUserId = userId;
            LastTenantId = tenantId;
            return Unavailable
                ? Task.FromException<AuthzSnapshot>(new AuthzUnavailableException())
                : Task.FromResult(snapshot);
        }
    }

    private sealed class StubUser(bool mfa) : ICurrentUser
    {
        public Guid UserId => TenantGateTests.UserId;
        public string? Email => null;
        public string? Acr => mfa ? "2" : "1";
        public bool HasRecentMfa => mfa;
    }

    private sealed class StubClassifier(bool privileged) : IPrivilegeClassifier
    {
        public bool IsPrivileged(string permission) => privileged;
    }
}
