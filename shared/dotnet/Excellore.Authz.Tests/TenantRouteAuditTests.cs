using Excellore.Authz;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Excellore.Authz.Tests;

public class TenantRouteAuditTests
{
    [Fact]
    public async Task Deliberately_unprotected_tenant_route_fails_startup_audit()
    {
        await using var app = CreateApp();
        app.MapGet("/v1/tenants/{tenantId:guid}/members", () => "unsafe");

        Assert.Throws<InvalidOperationException>(() => app.AssertTenantRoutesProtected());
    }

    [Fact]
    public async Task Tenant_gate_without_permission_fails_startup_audit()
    {
        await using var app = CreateApp();
        app.MapGroup("/v1/tenants/{tenantId:guid}")
            .AddTenantGate()
            .MapGet("/members", () => "unsafe");

        Assert.Throws<InvalidOperationException>(() => app.AssertTenantRoutesProtected());
    }

    [Fact]
    public async Task Protected_tenant_route_passes_startup_audit()
    {
        await using var app = CreateApp();
        app.MapGroup("/v1/tenants/{tenantId:guid}")
            .AddTenantGate()
            .MapGet("/members", () => "safe")
            .RequirePermission("profile.member.read");

        app.AssertTenantRoutesProtected();
    }

    [Fact]
    public async Task AllowAnonymous_cannot_override_group_authorization()
    {
        await using var app = CreateApp();
        app.MapGroup("/v1/tenants/{tenantId:guid}")
            .AddTenantGate()
            .MapGet("/members", () => "unsafe")
            .RequirePermission("profile.member.read")
            .AllowAnonymous();

        Assert.Throws<InvalidOperationException>(() => app.AssertTenantRoutesProtected());
    }

    [Fact]
    public async Task Explicit_member_only_route_passes_audit()
    {
        await using var app = CreateApp();
        app.MapGroup("/v1/tenants/{tenantId:guid}")
            .AddTenantGate()
            .MapGet("/me", () => "safe")
            .AllowAnyMember();

        app.AssertTenantRoutesProtected();
    }

    [Fact]
    public async Task Member_only_marker_cannot_override_a_permission()
    {
        await using var app = CreateApp();
        app.MapGroup("/v1/tenants/{tenantId:guid}")
            .AddTenantGate()
            .MapGet("/members", () => "unsafe")
            .RequirePermission("profile.member.read")
            .AllowAnyMember();

        Assert.Throws<InvalidOperationException>(() => app.AssertTenantRoutesProtected());
    }

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddExcelloreAuthz();
        return builder.Build();
    }
}
