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
builder.Services.AddScoped<PlatformTenantService>();
builder.Services.AddScoped<PlatformConsoleReadService>();
builder.Services.AddScoped<IdentityMirrorService>();
builder.Services.AddScoped<AccessResolutionService>();
builder.Services.AddScoped<AccessRoleReader>();
builder.Services.AddScoped<CustomRoleService>();
builder.Services.AddScoped<RoleAssignmentService>();
builder.Services.AddScoped<RoleAssignmentBatchService>();
builder.Services.AddScoped<AccessGroupService>();
builder.Services.AddScoped<InvitationAcceptanceService>();
builder.Services.AddScoped<InvitationManagementService>();
builder.Services.AddScoped<InvitationIssuanceService>();
builder.Services.AddScoped<InvitationDeliveryCoordinator>();
builder.Services.AddScoped<OwnerInvitationResendService>();
builder.Services.AddScoped<MemberLifecycleService>();
builder.Services.AddScoped<AccessTenantReadService>();
builder.Services.AddScoped<OwnershipTransferService>();
builder.Services.AddScoped<TenantSettingsService>();
builder.Services.AddScoped<AccessRequestService>();
builder.Services.AddScoped<TenantDeletionRequestService>();
builder.Services.AddScoped<AuditExportRequestService>();
builder.Services.AddScoped<AuditExportDownloadService>();
builder.Services.AddSingleton<IAuditExportObjectStore, S3AuditExportObjectStore>();
builder.Services.AddScoped<SupportGrantService>();
builder.Services.AddScoped<ISupportGrantUsageRecorder, SupportGrantUsageRecorder>();
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

app.MapGet("/v1/platform/tenants", async (PlatformTenantService service,
    CancellationToken cancellationToken) =>
    Results.Json(await service.ListAsync(cancellationToken)))
    .RequireAuthorization("Platform.Staff");
app.MapGet("/v1/platform/me", (HttpContext context) =>
    Results.Json(new { roles = context.User.FindAll("platform_roles")
        .Select(claim => claim.Value).Distinct().Order().ToArray() }))
    .RequireAuthorization("Platform.Staff");
app.MapGet("/v1/platform/permissions", async (PlatformConsoleReadService service,
    CancellationToken cancellationToken) =>
    Results.Json(await service.PermissionsAsync(cancellationToken)))
    .RequireAuthorization("Platform.Staff");
app.MapGet("/v1/platform/system-roles", async (PlatformConsoleReadService service,
    CancellationToken cancellationToken) =>
    Results.Json(await service.SystemRolesAsync(cancellationToken)))
    .RequireAuthorization("Platform.Staff");
app.MapGet("/v1/platform/support-roles", async (PlatformConsoleReadService service,
    CancellationToken cancellationToken) =>
    Results.Json(await service.SupportRolesAsync(cancellationToken)))
    .RequireAuthorization("Platform.Support");
app.MapGet("/v1/platform/audit-events", async (PlatformConsoleReadService service,
    CancellationToken cancellationToken) =>
    Results.Json(await service.AuditAsync(cancellationToken)))
    .RequireAuthorization("Platform.Auditor");
app.MapGet("/v1/platform/tenants/{id:guid}", async (Guid id,
    PlatformTenantService service, CancellationToken cancellationToken) =>
{
    var tenant = await service.GetAsync(id, cancellationToken);
    return tenant is null ? Results.NotFound() : Results.Json(tenant);
}).RequireAuthorization("Platform.Staff");
app.MapPatch("/v1/platform/tenants/{id:guid}", async (Guid id,
    UpdatePlatformTenantRequest request, PlatformTenantService service,
    CancellationToken cancellationToken) =>
    PlatformTenantResponses.Map(await service.UpdateAsync(id, request, cancellationToken)))
    .RequireAuthorization("Platform.Operator");
app.MapPost("/v1/platform/tenants/{id:guid}/suspend", async (Guid id,
    PlatformTenantActionRequest request, PlatformTenantService service,
    CancellationToken cancellationToken) =>
    PlatformTenantResponses.Map(await service.SuspendAsync(id, request, cancellationToken)))
    .RequireAuthorization("Platform.Suspend");
app.MapPost("/v1/platform/tenants/{id:guid}/reactivate", async (Guid id,
    PlatformTenantActionRequest request, PlatformTenantService service,
    CancellationToken cancellationToken) =>
    PlatformTenantResponses.Map(await service.ReactivateAsync(id, request, cancellationToken)))
    .RequireAuthorization("Platform.Operator");
app.MapPost("/v1/platform/tenants/{id:guid}/deletion-requests", async (Guid id,
    PlatformTenantActionRequest request, PlatformTenantService service,
    CancellationToken cancellationToken) =>
    PlatformTenantResponses.Map(await service.RequestDeletionAsync(id, request, cancellationToken)))
    .RequireAuthorization("Platform.Operator");
app.MapPut("/v1/platform/tenants/{id:guid}/apps/{appId}", async (Guid id,
    string appId, SetPlatformTenantAppRequest request, PlatformTenantService service,
    CancellationToken cancellationToken) =>
    PlatformTenantResponses.Map(await service.SetAppAsync(id, appId, request, cancellationToken)))
    .RequireAuthorization("Platform.Operator");

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
app.MapPost("/v1/platform/support-grants", async (CreateSupportGrantRequest request,
    SupportGrantService service, CancellationToken cancellationToken) =>
    SupportGrantResponses.Map(await service.RequestAsync(request, cancellationToken)))
    .RequireAuthorization("Platform.Support");
app.MapPost("/v1/platform/support-requests", async (CreateSupportGrantRequest request,
    SupportGrantService service, CancellationToken cancellationToken) =>
    SupportGrantResponses.Map(await service.RequestAsync(request, cancellationToken)))
    .RequireAuthorization("Platform.Support");
app.MapGet("/v1/platform/tenants/{id:guid}/support-requests", async (Guid id,
    SupportGrantService service, CancellationToken cancellationToken) =>
    Results.Json(await service.ListMineAsync(id, cancellationToken)))
    .RequireAuthorization("Platform.Support");

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

app.MapPost("/internal/v1/users/invitations:list", async (TenantListRequest request,
    InvitationManagementService service, HttpContext context, CancellationToken cancellationToken) =>
{
    if (request.UserId == Guid.Empty)
        return Results.Problem(statusCode: 400,
            type: "urn:excellore:problem:invalid-invitation-list-request", title: "Invalid invitation list request");
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(await service.ListForUserAsync(request.UserId, cancellationToken));
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
app.MapGet("/v1/me/tenants", async (HttpContext context, ICurrentUser user,
    AccessResolutionService service, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(await service.ListTenantsAsync(user.UserId, cancellationToken));
}).RequireAuthorization();

var tenantRoutes = app.MapGroup("/v1/tenants/{tenantId:guid}")
    .AddTenantGate().AddTenantRateLimit().AddSupportGrantUsageAudit();
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
tenantRoutes.MapGet("/members", async (HttpContext context, AccessTenantReadService service,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(await service.ListMembersAsync(cancellationToken));
}).RequirePermission("access.member.read");
tenantRoutes.MapPost("/members:lookup", async (MemberLookupRequest request,
    HttpContext context, AccessTenantReadService service, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var member = await service.GetMemberAsync(request.UserId, cancellationToken);
    return member is null ? Results.NotFound() : Results.Json(member);
}).RequirePermission("access.member.read");
tenantRoutes.MapGet("/apps", async (AccessTenantReadService service,
    CancellationToken cancellationToken) =>
    Results.Json(await service.ListAppsAsync(cancellationToken)))
    .RequirePermission("access.apps.read");
tenantRoutes.MapGet("/audit-events", async (AccessTenantReadService service,
    CancellationToken cancellationToken) =>
    Results.Json(await service.ListAuditAsync(cancellationToken)))
    .RequirePermission("access.audit.read");
tenantRoutes.MapPost("/audit-events/exports", async (AuditExportRequestService service,
    CancellationToken cancellationToken) =>
    AuditExportRequestResponses.Map(await service.RequestAsync(cancellationToken)))
    .RequirePermission("access.audit.read");
tenantRoutes.MapGet("/audit-events/exports/{exportId:guid}", async (
    Guid exportId, AuditExportRequestService service, CancellationToken cancellationToken) =>
{
    var export = await service.GetAsync(exportId, cancellationToken);
    return export is null ? Results.NotFound() : Results.Json(export);
}).RequirePermission("access.audit.read");
tenantRoutes.MapGet("/audit-events/exports/{exportId:guid}/download", async (
    Guid exportId, AuditExportDownloadService service, CancellationToken cancellationToken) =>
{
    var result = await service.DownloadAsync(exportId, cancellationToken);
    return result.Status switch
    {
        AuditExportDownloadStatus.Ready => Results.File(result.Data!,
            "application/x-ndjson", "audit-export.jsonl"),
        AuditExportDownloadStatus.StepUpRequired => Results.Problem(statusCode: 403,
            type: "urn:excellore:problem:step-up-required", title: "Step-up required"),
        _ => Results.NotFound()
    };
}).RequirePermission("access.audit.read");
tenantRoutes.MapGet("/settings", async (TenantSettingsService service,
    CancellationToken cancellationToken) =>
{
    var settings = await service.GetAsync(cancellationToken);
    return settings is null ? Results.NotFound() : Results.Json(settings);
}).RequirePermission("access.settings.read");
tenantRoutes.MapPatch("/settings", async (ChangeTenantSettingsRequest request,
    TenantSettingsService service, CancellationToken cancellationToken) =>
    TenantSettingsResponses.Map(await service.ChangeAsync(request, cancellationToken)))
    .RequirePermission("access.settings.manage");
tenantRoutes.MapPost("/access-requests", async (CreateAccessRequest request,
    AccessRequestService service, CancellationToken cancellationToken) =>
    AccessRequestResponses.Map(await service.CreateAsync(request, cancellationToken)))
    .RequirePermission("access.request.create");
tenantRoutes.MapGet("/access-requests", async (AccessRequestService service,
    CancellationToken cancellationToken) =>
    Results.Json(await service.ListAsync(cancellationToken)))
    .RequirePermission("access.request.approve");
tenantRoutes.MapPost("/access-requests/{requestId:guid}/approve", async (
    Guid requestId, DecideAccessRequest request, AccessRequestService service,
    CancellationToken cancellationToken) =>
    AccessRequestResponses.Map(await service.ApproveAsync(requestId, request, cancellationToken)))
    .RequirePermission("access.request.approve");
tenantRoutes.MapPost("/access-requests/{requestId:guid}/reject", async (
    Guid requestId, DecideAccessRequest request, AccessRequestService service,
    CancellationToken cancellationToken) =>
    AccessRequestResponses.Map(await service.RejectAsync(requestId, request, cancellationToken)))
    .RequirePermission("access.request.approve");
tenantRoutes.MapPost("/deletion-requests", async (RequestTenantDeletion request,
    TenantDeletionRequestService service, CancellationToken cancellationToken) =>
    TenantDeletionRequestResponses.Map(await service.RequestAsync(request, cancellationToken)))
    .RequirePermission("access.tenant.delete");
tenantRoutes.MapGet("/support-grants", async (HttpContext context,
    SupportGrantService service, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(await service.ListAsync(cancellationToken));
}).RequirePermission("access.supportgrant.manage");
tenantRoutes.MapPost("/support-grants/{grantId:guid}/approve", async (Guid grantId,
    DecideSupportGrantRequest request, SupportGrantService service,
    CancellationToken cancellationToken) =>
    SupportGrantResponses.Map(await service.ApproveAsync(grantId, request, cancellationToken)))
    .RequirePermission("access.supportgrant.manage");
tenantRoutes.MapPost("/support-grants/{grantId:guid}/revoke", async (Guid grantId,
    DecideSupportGrantRequest request, SupportGrantService service,
    CancellationToken cancellationToken) =>
    SupportGrantResponses.Map(await service.RevokeAsync(grantId, request, cancellationToken)))
    .RequirePermission("access.supportgrant.manage");
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
tenantRoutes.MapPost("/ownership-transfers", async (NominateOwnerRequest request,
    OwnershipTransferService service, CancellationToken cancellationToken) =>
    OwnershipTransferResponses.Map(await service.NominateAsync(request, cancellationToken)))
    .RequirePermission("access.tenant.transfer");
tenantRoutes.MapPost("/ownership-transfers/{transferId:guid}/accept", async (
    Guid transferId, OwnershipTransferService service, CancellationToken cancellationToken) =>
    OwnershipTransferResponses.Map(await service.AcceptAsync(transferId, cancellationToken)))
    .RequirePermission("access.app.access");
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
tenantRoutes.MapGet("/role-assignments", async (AccessTenantReadService service,
    CancellationToken cancellationToken) =>
    Results.Json(await service.ListRoleAssignmentsAsync(cancellationToken)))
    .RequirePermission("access.assignment.manage");
tenantRoutes.MapDelete("/role-assignments/{assignmentId:guid}", async (Guid assignmentId,
    [FromBody] RevokeRoleRequest request, RoleAssignmentService service,
    CancellationToken cancellationToken) =>
    RoleAssignmentResponses.Map(await service.RevokeAsync(assignmentId, request, cancellationToken)))
    .RequirePermission("access.assignment.manage");
tenantRoutes.MapPost("/role-assignment-batches", async (
    CreateRoleAssignmentBatchRequest request, RoleAssignmentBatchService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.CreateAsync(request, cancellationToken);
    return result.Status switch
    {
        RoleAssignmentBatchStatus.Accepted => Results.Json(result.Batch,
            statusCode: StatusCodes.Status202Accepted),
        RoleAssignmentBatchStatus.Invalid => Results.Problem(statusCode: 400,
            type: "urn:excellore:problem:invalid-assignment-batch", title: "Invalid assignment batch"),
        RoleAssignmentBatchStatus.Forbidden => Results.Problem(statusCode: 403,
            type: "urn:excellore:problem:assignment-batch-denied", title: "Assignment batch denied"),
        RoleAssignmentBatchStatus.StepUpRequired => Results.Problem(statusCode: 403,
            type: "urn:excellore:problem:step-up-required", title: "Step-up required"),
        RoleAssignmentBatchStatus.NotFound => Results.NotFound(),
        RoleAssignmentBatchStatus.Conflict => Results.Problem(statusCode: 409,
            type: "urn:excellore:problem:assignment-batch-conflict", title: "Assignment batch already pending"),
        _ => Results.StatusCode(500)
    };
}).RequirePermission("access.assignment.manage");
tenantRoutes.MapGet("/role-assignment-batches/{batchId:guid}", async (
    Guid batchId, RoleAssignmentBatchService service, CancellationToken cancellationToken) =>
{
    var batch = await service.GetAsync(batchId, cancellationToken);
    return batch is null ? Results.NotFound() : Results.Json(batch);
}).RequirePermission("access.assignment.manage");
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
