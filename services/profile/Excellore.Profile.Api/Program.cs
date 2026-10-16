using Excellore.Auth;
using Excellore.Authz;
using Excellore.Profile.Application;
using Excellore.Profile.Infrastructure;
using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var localHttp = builder.Environment.IsDevelopment() &&
    string.Equals(builder.Configuration["EXCELLORE_INSECURE_LOCAL_DEV"], "true",
        StringComparison.OrdinalIgnoreCase);
var connectionString = builder.Configuration.GetConnectionString("Profile")
    ?? throw new InvalidOperationException("ConnectionStrings:Profile is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddScoped<ProfileRepository>();
builder.Services.AddSingleton<IAvatarObjectStore, S3AvatarObjectStore>();
builder.Services.AddScoped<OwnIdentityService>();
builder.Services.AddHttpClient<IOwnIdentityClient, KeycloakOwnIdentityClient>(client =>
    client.Timeout = TimeSpan.FromSeconds(10)).RemoveAllLoggers();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration["Authz:Valkey"] ??
        throw new InvalidOperationException("Authz:Valkey is required.")));
builder.Services.AddExcelloreAuthz();
builder.Services.AddValkeyAuthzResolver(builder.Configuration, "profile", localHttp);
var broker = builder.Configuration["Authz:RabbitMq"];
if (!Uri.TryCreate(broker, UriKind.Absolute, out var brokerUri))
    throw new InvalidOperationException("Authz:RabbitMq is required.");
builder.Services.AddRabbitMqAuthzChangedConsumer("profile", brokerUri);
builder.Services.AddExcelloreAuthentication(builder.Configuration, "api-profile", !localHttp);
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

var me = app.MapGroup("/v1/me").RequireAuthorization();
me.MapGet("/profile", async (ICurrentUser user, ProfileRepository profiles,
    HttpContext context, CancellationToken ct) =>
{
    await profiles.EnsureMirrorAsync(user.UserId, context.User.FindFirst("given_name")?.Value,
        context.User.FindFirst("family_name")?.Value, context.User.FindFirst("locale")?.Value, ct);
    return Results.Ok(await profiles.GetOwnAsync(user.UserId, ct));
});
me.MapPatch("/profile", async (UpdateOwnProfile input, ICurrentUser user,
    ProfileRepository profiles, OwnIdentityService identity, CancellationToken ct) =>
{
    if (!ProfileValidation.ValidOwn(input)) return Invalid();
    if (!await identity.UpdateAsync(input, ct))
        return Results.Problem(statusCode: 503,
            type: "urn:excellore:problem:identity-unavailable",
            title: "Identity update unavailable");
    return Results.Ok(await profiles.UpdateOwnAsync(user.UserId, input, ct));
});
me.MapGet("/preferences", async (ICurrentUser user, ProfileRepository profiles, CancellationToken ct) =>
    Results.Ok(await profiles.GetPreferencesAsync(user.UserId, ct)));
me.MapPatch("/preferences", async (UpdatePreferences input, ICurrentUser user,
    ProfileRepository profiles, CancellationToken ct) =>
{
    if (!ProfileValidation.ValidPreferences(input)) return Invalid();
    return Results.Ok(await profiles.UpdatePreferencesAsync(user.UserId, input, ct));
});
me.MapGet("/security", (ICurrentUser user) =>
    Results.Ok(new { mfaAuthenticated = user.HasRecentMfa,
        actions = new[] { "UPDATE_PASSWORD", "UPDATE_EMAIL", "CONFIGURE_TOTP" } }));
me.MapPost("/avatar/uploads", async (CreateAvatarUpload input, ICurrentUser user,
    ProfileRepository profiles, CancellationToken ct) =>
{
    if (input.ContentType is not ("image/png" or "image/jpeg" or "image/webp") ||
        input.SizeBytes is < 16 or > AvatarProcessor.MaxInputBytes) return Invalid();
    var id = await profiles.CreateAvatarUploadAsync(user.UserId, input, ct);
    return Results.Ok(new { id });
});
me.MapPost("/avatar/uploads/{id:guid}/complete", async (Guid id, CompleteAvatarUpload input,
    ICurrentUser user, ProfileRepository profiles, IAvatarObjectStore objects, CancellationToken ct) =>
{
    if (input.Base64Data is null or { Length: > 960000 }) return Invalid();
    byte[] data;
    try { data = Convert.FromBase64String(input.Base64Data); }
    catch (FormatException) { return Invalid(); }
    return await profiles.CompleteAvatarUploadAsync(user.UserId, id, data, objects, ct)
        ? Results.NoContent() : Invalid();
});
me.MapGet("/avatar", async (ICurrentUser user, ProfileRepository profiles,
    IAvatarObjectStore objects, HttpContext context, CancellationToken ct) =>
{
    var image = await profiles.GetAvatarAsync(user.UserId, objects, ct);
    if (image is null) return Results.NotFound();
    context.Response.Headers.CacheControl = "private, no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(image, "image/webp");
});
me.MapDelete("/avatar", async (ICurrentUser user, ProfileRepository profiles,
    IAvatarObjectStore objects, CancellationToken ct) =>
{
    await profiles.RemoveAvatarAsync(user.UserId, objects, ct);
    return Results.NoContent();
});
me.MapGet("/sessions", async (OwnIdentityService identity, CancellationToken ct) =>
{
    var sessions = await identity.GetSessionsAsync(ct);
    return sessions is null ? Results.Problem(statusCode: 503,
        type: "urn:excellore:problem:identity-unavailable", title: "Sessions unavailable")
        : Results.Ok(sessions);
});
me.MapDelete("/sessions/{id:guid}", async (Guid id, OwnIdentityService identity,
    CancellationToken ct) =>
    await identity.RevokeSessionAsync(id, ct) ? Results.NoContent() : Results.NotFound());
me.MapPost("/data-exports", async (ICurrentUser user, ProfileRepository profiles, CancellationToken ct) =>
    Results.Accepted(value: new { id = await profiles.RequestExportAsync(user.UserId, ct) }));
me.MapGet("/data-exports/{id:guid}", async (Guid id, ICurrentUser user,
    ProfileRepository profiles, CancellationToken ct) =>
{
    var status = await profiles.GetExportStatusAsync(user.UserId, id, ct);
    return status is null ? Results.NotFound() : Results.Ok(new { id, status });
});
me.MapPost("/deletion-requests", async (ICurrentUser user, ProfileRepository profiles, CancellationToken ct) =>
    user.HasRecentMfa
        ? Results.Accepted(value: new { id = await profiles.RequestDeletionAsync(user.UserId, ct) })
        : Results.Problem(statusCode: 403, type: "urn:excellore:problem:step-up-required",
            title: "Step-up authentication required"));

var tenant = app.MapGroup("/v1/tenants/{tenantId:guid}").AddTenantGate();
tenant.MapGet("/members/me", async (Guid tenantId, ICurrentUser user,
    ProfileRepository profiles, CancellationToken ct) =>
    Results.Ok(await profiles.GetMemberAsync(tenantId, user.UserId, true, ct)))
    .RequirePermission("profile.app.access");
tenant.MapPatch("/members/me", async (Guid tenantId, UpdateMemberProfile input,
    ICurrentUser user, ProfileRepository profiles, CancellationToken ct) =>
{
    if (!ProfileValidation.ValidMember(input)) return Invalid();
    var result = await profiles.UpdateMemberAsync(tenantId, user.UserId, user.UserId,
        input, null, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).RequirePermission("profile.app.access");
tenant.MapGet("/members", async (Guid tenantId, Guid? after, int? limit,
    ProfileRepository profiles, CancellationToken ct) =>
    Results.Ok(await profiles.ListMembersAsync(tenantId, after, limit ?? 50, ct)))
    .RequirePermission("profile.member.read");
tenant.MapPost("/members:lookup", async (Guid tenantId, MemberLookup input,
    ProfileRepository profiles, CancellationToken ct) =>
{
    if (input.UserId == Guid.Empty) return Invalid();
    var member = await profiles.GetMemberAsync(tenantId, input.UserId, false, ct);
    return member is null || member.DirectoryVisibility == "hidden"
        ? Results.NotFound() : Results.Ok(member);
}).RequirePermission("profile.member.read");
tenant.MapPatch("/members", async (Guid tenantId, AdminMemberUpdate input,
    ICurrentUser user, ProfileRepository profiles, CancellationToken ct) =>
{
    if (input.UserId == Guid.Empty || input.UserId == user.UserId ||
        string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 500) return Invalid();
    var update = new UpdateMemberProfile(input.JobTitle, input.Department, input.WorkPhone,
        input.DirectoryVisibility);
    if (!ProfileValidation.ValidMember(update)) return Invalid();
    var member = await profiles.UpdateMemberAsync(tenantId, input.UserId, user.UserId,
        update, input.Reason, ct);
    return member is null ? Results.NotFound() : Results.Ok(member);
}).RequirePermission("profile.member.manage");
app.AssertTenantRoutesProtected();
app.Run();

static IResult Invalid() => Results.Problem(statusCode: 400,
    type: "urn:excellore:problem:validation", title: "Invalid request");

public partial class Program { }
