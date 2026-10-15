using Excellore.Access.Application;
using Excellore.Access.Api;
using Excellore.Access.Infrastructure;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;
using System.Net.Mail;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Access")
    ?? throw new InvalidOperationException("ConnectionStrings:Access is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddScoped<CatalogSyncService>();
builder.Services.AddScoped<TenantProvisioningService>();
builder.Services.AddScoped<IdentityMirrorService>();
builder.Services.AddScoped<AccessResolutionService>();
builder.Services.AddScoped<AccessRoleReader>();
builder.Services.AddScoped<CustomRoleService>();
builder.Services.AddScoped<RoleAssignmentService>();
builder.Services.AddScoped<AccessGroupService>();
builder.Services.AddScoped<InvitationAcceptanceService>();
builder.Services.AddScoped<InvitationManagementService>();
builder.Services.AddScoped<InvitationIssuanceService>();
builder.Services.AddScoped<InvitationDeliveryCoordinator>();
builder.Services.AddScoped<OwnerInvitationResendService>();
builder.Services.AddScoped<MemberLifecycleService>();
builder.Services.AddScoped<IAuthzResolver, AccessAuthorityResolver>();
builder.Services.AddScoped<IPrivilegeClassifier, AccessPrivilegeClassifier>();
builder.Services.AddExcelloreAuthz();
builder.Services.AddProblemDetails();
var allowLocalHttp = builder.Environment.IsDevelopment() &&
    string.Equals(builder.Configuration["EXCELLORE_INSECURE_LOCAL_DEV"], "true", StringComparison.OrdinalIgnoreCase);
builder.Services.AddSingleton(new IdentityAdminOptions(builder.Configuration["Auth:Authority"],
    builder.Configuration["IdentityAdmin:ClientSecret"] ?? builder.Configuration["SVC_ACCESS_ADMIN_SECRET"],
    builder.Configuration["Invitation:AcceptanceOrigin"], allowLocalHttp));
builder.Services.AddSingleton<IdentityAdminCircuit>();
builder.Services.AddHttpClient<IIdentityAdmin, KeycloakIdentityAdmin>(client =>
    client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<IInvitationDelivery>(_ => new SmtpInvitationDelivery(
    builder.Configuration["Invitation:SmtpHost"],
    int.TryParse(builder.Configuration["Invitation:SmtpPort"], out var port) ? port : 0,
    builder.Configuration["Invitation:From"],
    builder.Configuration["Invitation:AcceptanceOrigin"],
    bool.TryParse(builder.Configuration["Invitation:SmtpTls"], out var tls) && tls,
    allowLocalHttp));
builder.Services.AddExcelloreAuthentication(builder.Configuration, "api-access", !allowLocalHttp);

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/v1") || context.User.Identity?.IsAuthenticated != true)
    {
        await next(context);
        return;
    }
    var email = context.User.FindFirst("email")?.Value;
    if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId) ||
        email is not { Length: > 0 and <= 254 } ||
        !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    var identity = new VerifiedIdentity(userId, email,
        bool.TryParse(context.User.FindFirst("email_verified")?.Value, out var verified) && verified,
        context.User.FindFirst("given_name")?.Value,
        context.User.FindFirst("family_name")?.Value);
    var result = await context.RequestServices.GetRequiredService<IdentityMirrorService>()
        .UpsertAsync(identity, context.RequestAborted);
    if (result == IdentityMirrorResult.Conflict)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        return;
    }
    await next(context);
});

app.MapPost("/v1/platform/tenants", async (CreateTenantRequest request, HttpContext context,
    TenantProvisioningService service, CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var actorSub))
        return Results.Unauthorized();
    var result = await service.CreateAsync(request, actorSub, cancellationToken,
        context.User.FindFirst("acr")?.Value);
    return result.Status switch
    {
        CreateTenantStatus.Created => Results.Json(result.Tenant, statusCode: StatusCodes.Status201Created),
        CreateTenantStatus.Invalid => Results.Problem(statusCode: 400,
            type: "urn:excellore:problem:invalid-tenant", title: "Invalid tenant request"),
        CreateTenantStatus.SlugTaken => Results.Problem(statusCode: 409,
            type: "urn:excellore:problem:tenant-slug-taken", title: "Tenant slug is unavailable"),
        CreateTenantStatus.CatalogUnavailable => Results.Problem(statusCode: 503,
            type: "urn:excellore:problem:catalog-unavailable", title: "Tenant catalog is unavailable"),
        _ => Results.Problem(statusCode: 500)
    };
}).RequireAuthorization("Platform.Operator");
app.MapPost("/v1/platform/tenants/{id:guid}/owner-invitation:resend",
    async (Guid id, HttpContext context, OwnerInvitationResendService service,
        CancellationToken cancellationToken) =>
    {
        if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var actorSub))
            return Results.Unauthorized();
        var result = await service.ResendAsync(id,
            new IdentityAdminActor(actorSub, context.User.FindFirst("acr")?.Value), cancellationToken);
        return result == OwnerInvitationResendStatus.Accepted
            ? Results.Accepted() : Results.NotFound();
    }).RequireAuthorization("Platform.Operator");

app.MapPut("/internal/v1/catalog/{app}", async (
    string app, PermissionCatalog catalog, HttpContext context,
    CatalogSyncService service, CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var actorSub))
        return Results.Unauthorized();

    var result = await service.SyncAsync(app, catalog, actorSub, cancellationToken);
    return result switch
    {
        CatalogSyncResult.Updated or CatalogSyncResult.Unchanged => Results.NoContent(),
        CatalogSyncResult.Invalid => Results.Problem(statusCode: 400,
            type: "urn:excellore:problem:invalid-catalog", title: "Invalid permission catalog"),
        CatalogSyncResult.Conflict => Results.Problem(statusCode: 409,
            type: "urn:excellore:problem:catalog-conflict", title: "Catalog change conflicts with assigned permissions"),
        CatalogSyncResult.UnknownApp => Results.NotFound(),
        _ => Results.Problem(statusCode: 500)
    };
}).RequireAuthorization("Internal.Catalog");

app.MapPost("/internal/v1/authz/resolve", async (ResolveRequest request, HttpContext context,
    AccessResolutionService service, CancellationToken cancellationToken) =>
{
    if (request.UserId == Guid.Empty || request.TenantId == Guid.Empty)
        return Results.Problem(statusCode: 400,
            type: "urn:excellore:problem:invalid-resolve-request", title: "Invalid resolve request");
    var result = await service.ResolveAsync(request.UserId, request.TenantId, cancellationToken);
    if (!result.Allowed)
        return Results.Json(new { result.TenantId, decision = "deny", permissions = Array.Empty<string>() });
    context.Response.Headers.ETag = $"\"{result.Version}\"";
    return Results.Json(new
    {
        result.TenantId, decision = "allow", tenantStatus = "active", membershipStatus = "active",
        result.Roles, result.Permissions, result.Apps, result.Version, result.ResolvedAt
    });
}).RequireAuthorization("Internal.Authz");

app.MapPost("/internal/v1/users/tenants:list", async (TenantListRequest request,
    AccessResolutionService service, CancellationToken cancellationToken) =>
{
    if (request.UserId == Guid.Empty)
        return Results.Problem(statusCode: 400,
            type: "urn:excellore:problem:invalid-tenant-list-request", title: "Invalid tenant list request");
    return Results.Json(await service.ListTenantsAsync(request.UserId, cancellationToken));
}).RequireAuthorization("Internal.Authz");

app.MapPost("/internal/v1/tenants:resolve-slug", async (ResolveSlugRequest request,
    AccessResolutionService service, CancellationToken cancellationToken) =>
{
    var tenant = await service.ResolveSlugAsync(request.UserId, request.Slug, cancellationToken);
    return tenant is null ? Results.NotFound() : Results.Json(tenant);
}).RequireAuthorization("Internal.Authz");

app.MapPost("/v1/me/invitations/accept", async (AcceptInvitationRequest request,
    InvitationAcceptanceService service, CancellationToken cancellationToken) =>
{
    var result = await service.AcceptAsync(request, cancellationToken);
    return result.Status switch
    {
        InvitationAcceptanceStatus.Accepted => Results.Json(new { result.TenantId }),
        InvitationAcceptanceStatus.Invalid => Results.Problem(statusCode: 400,
            type: "urn:excellore:problem:invalid-invitation", title: "Invalid invitation"),
        InvitationAcceptanceStatus.Unavailable => Results.NotFound(),
        InvitationAcceptanceStatus.Conflict => Results.Problem(statusCode: 409,
            type: "urn:excellore:problem:invitation-conflict", title: "Invitation unavailable"),
        _ => Results.StatusCode(500)
    };
}).RequireAuthorization();
app.MapGet("/v1/me/invitations", async (HttpContext context, InvitationManagementService service,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(await service.ListMineAsync(cancellationToken));
})
    .RequireAuthorization();

var tenantRoutes = app.MapGroup("/v1/tenants/{tenantId:guid}").AddTenantGate();
tenantRoutes.MapGet("/me/access", async (ICurrentUser user, ITenantContextAccessor tenant,
    AccessResolutionService service, CancellationToken cancellationToken) =>
{
    var result = await service.ResolveAsync(user.UserId, tenant.TenantId, cancellationToken);
    return result.Allowed ? Results.Json(new
    {
        result.TenantId, result.Roles, result.Permissions, result.Apps,
        result.Version, result.ResolvedAt
    }) : Results.NotFound();
}).RequirePermission("access.app.access");
tenantRoutes.MapGet("/invitations", async (HttpContext context, InvitationManagementService service,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(await service.ListAsync(cancellationToken));
})
    .RequirePermission("access.member.invite");
tenantRoutes.MapPost("/invitations", async (CreateInvitationRequest request,
    InvitationIssuanceService service, CancellationToken cancellationToken) =>
    InvitationIssuanceResponses.Map(await service.CreateAsync(request, cancellationToken)))
    .RequirePermission("access.member.invite");
tenantRoutes.MapPost("/invitations/{invitationId:guid}/resend", async (Guid invitationId,
    InvitationIssuanceService service, CancellationToken cancellationToken) =>
    InvitationIssuanceResponses.Map(await service.ResendAsync(invitationId, cancellationToken)))
    .RequirePermission("access.member.invite");
tenantRoutes.MapDelete("/invitations/{invitationId:guid}", async (Guid invitationId,
    InvitationManagementService service, CancellationToken cancellationToken) =>
{
    var status = await service.RevokeAsync(invitationId, cancellationToken);
    return status switch
    {
        InvitationRevocationStatus.Revoked => Results.NoContent(),
        InvitationRevocationStatus.NotFound => Results.NotFound(),
        InvitationRevocationStatus.Forbidden => Results.Forbid(),
        _ => Results.StatusCode(500)
    };
}).RequirePermission("access.member.invite");
tenantRoutes.MapPost("/members:suspend", async (MemberChangeRequest request,
    MemberLifecycleService service, CancellationToken cancellationToken) =>
    MemberLifecycleResponses.Map(await service.SuspendAsync(request, cancellationToken)))
    .RequirePermission("access.member.suspend");
tenantRoutes.MapPost("/members:reactivate", async (MemberChangeRequest request,
    MemberLifecycleService service, CancellationToken cancellationToken) =>
    MemberLifecycleResponses.Map(await service.ReactivateAsync(request, cancellationToken)))
    .RequirePermission("access.member.suspend");
tenantRoutes.MapDelete("/members", async ([FromBody] MemberChangeRequest request,
    MemberLifecycleService service, CancellationToken cancellationToken) =>
    MemberLifecycleResponses.Map(await service.RemoveAsync(request, cancellationToken)))
    .RequirePermission("access.member.remove");
tenantRoutes.MapGet("/roles", async (AccessRoleReader service, CancellationToken cancellationToken) =>
    Results.Json(await service.ListRolesAsync(cancellationToken)))
    .RequirePermission("access.role.read");
tenantRoutes.MapGet("/roles/{roleId:guid}", async (Guid roleId, AccessRoleReader service,
    CancellationToken cancellationToken) =>
{
    var role = await service.GetRoleAsync(roleId, cancellationToken);
    return role is null ? Results.NotFound() : Results.Json(role);
}).RequirePermission("access.role.read");
tenantRoutes.MapGet("/permissions", async (AccessRoleReader service, CancellationToken cancellationToken) =>
    Results.Json(await service.ListPermissionsAsync(cancellationToken)))
    .RequirePermission("access.role.read");
tenantRoutes.MapPost("/roles", async (CustomRoleRequest request, CustomRoleService service,
    CancellationToken cancellationToken) =>
    CustomRoleResponses.Map(await service.CreateAsync(request, cancellationToken)))
    .RequirePermission("access.role.manage");
tenantRoutes.MapPut("/roles/{roleId:guid}", async (Guid roleId, CustomRoleRequest request,
    CustomRoleService service, CancellationToken cancellationToken) =>
    CustomRoleResponses.Map(await service.UpdateAsync(roleId, request, cancellationToken)))
    .RequirePermission("access.role.manage");
tenantRoutes.MapDelete("/roles/{roleId:guid}", async (Guid roleId, CustomRoleService service,
    CancellationToken cancellationToken) =>
    CustomRoleResponses.Map(await service.DeleteAsync(roleId, cancellationToken)))
    .RequirePermission("access.role.manage");
tenantRoutes.MapPost("/role-assignments", async (GrantRoleRequest request,
    RoleAssignmentService service, CancellationToken cancellationToken) =>
    RoleAssignmentResponses.Map(await service.GrantAsync(request, cancellationToken)))
    .RequirePermission("access.assignment.manage");
tenantRoutes.MapDelete("/role-assignments/{assignmentId:guid}", async (Guid assignmentId,
    [FromBody] RevokeRoleRequest request, RoleAssignmentService service,
    CancellationToken cancellationToken) =>
    RoleAssignmentResponses.Map(await service.RevokeAsync(assignmentId, request, cancellationToken)))
    .RequirePermission("access.assignment.manage");
tenantRoutes.MapGet("/groups", async (AccessGroupService service,
    CancellationToken cancellationToken) => Results.Json(await service.ListAsync(cancellationToken)))
    .RequirePermission("access.group.manage");
tenantRoutes.MapPost("/groups", async (CreateGroupRequest request, AccessGroupService service,
    CancellationToken cancellationToken) =>
    GroupResponses.Create(await service.CreateAsync(request, cancellationToken)))
    .RequirePermission("access.group.manage");
tenantRoutes.MapPut("/groups/{groupId:guid}/members", async (Guid groupId,
    [FromBody] ChangeGroupMemberRequest request, AccessGroupService service,
    CancellationToken cancellationToken) =>
    GroupResponses.Map(await service.AddMemberAsync(groupId, request, cancellationToken)))
    .RequirePermission("access.group.manage");
tenantRoutes.MapDelete("/groups/{groupId:guid}/members", async (Guid groupId,
    [FromBody] ChangeGroupMemberRequest request, AccessGroupService service,
    CancellationToken cancellationToken) =>
    GroupResponses.Map(await service.RemoveMemberAsync(groupId, request, cancellationToken)))
    .RequirePermission("access.group.manage");
tenantRoutes.MapPost("/groups/{groupId:guid}/role-assignments", async (Guid groupId,
    GrantGroupRoleRequest request, AccessGroupService service,
    CancellationToken cancellationToken) =>
    GroupResponses.GrantRole(await service.GrantRoleAsync(groupId, request, cancellationToken)))
    .RequirePermission("access.group.manage");
tenantRoutes.MapDelete("/groups/{groupId:guid}/role-assignments/{assignmentId:guid}",
    async (Guid groupId, Guid assignmentId, [FromBody] RevokeGroupRoleRequest request,
    AccessGroupService service, CancellationToken cancellationToken) =>
    GroupResponses.Map(await service.RevokeRoleAsync(groupId, assignmentId,
        request, cancellationToken)))
    .RequirePermission("access.group.manage");

app.AssertTenantRoutesProtected();

app.Run();
