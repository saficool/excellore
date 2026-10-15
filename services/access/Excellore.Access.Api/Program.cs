using Excellore.Access.Application;
using Excellore.Access.Infrastructure;
using Excellore.Auth;
using Npgsql;
using System.Net.Mail;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Access")
    ?? throw new InvalidOperationException("ConnectionStrings:Access is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddScoped<CatalogSyncService>();
builder.Services.AddScoped<TenantProvisioningService>();
builder.Services.AddScoped<IdentityMirrorService>();
builder.Services.AddProblemDetails();
var allowLocalHttp = builder.Environment.IsDevelopment() &&
    string.Equals(builder.Configuration["EXCELLORE_INSECURE_LOCAL_DEV"], "true", StringComparison.OrdinalIgnoreCase);
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
    var result = await service.CreateAsync(request, actorSub, cancellationToken);
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

app.Run();
