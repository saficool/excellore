using Excellore.Access.Application;
using Excellore.Access.Infrastructure;
using Excellore.Auth;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Access")
    ?? throw new InvalidOperationException("ConnectionStrings:Access is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddScoped<CatalogSyncService>();
builder.Services.AddProblemDetails();
var allowLocalHttp = builder.Environment.IsDevelopment() &&
    string.Equals(builder.Configuration["EXCELLORE_INSECURE_LOCAL_DEV"], "true", StringComparison.OrdinalIgnoreCase);
builder.Services.AddExcelloreAuthentication(builder.Configuration, "api-access", !allowLocalHttp);

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

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
