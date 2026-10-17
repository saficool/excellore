using Excellore.Notifications.Domain;
using Excellore.Notifications.Infrastructure;
using Excellore.Platform;
using Npgsql;
using Resend;
using Resend.Webhooks;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);
var connectionString = builder.Configuration.GetConnectionString("Notifications")
    ?? throw new InvalidOperationException("ConnectionStrings:Notifications is required.");
var dataSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<ProviderWebhookDatabase>();
builder.Services.AddExcelloreHealthChecks()
    .AddAsyncCheck("postgres", async cancellationToken =>
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(cancellationToken);
        return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
    });

var resendEnabled = builder.Configuration.GetValue<bool>("ProviderWebhooks:ResendEnabled");
var messageIdDomain = builder.Configuration["ProviderWebhooks:MessageIdDomain"];
if (resendEnabled)
{
    var signingSecret = builder.Configuration["ProviderWebhooks:ResendSigningSecret"];
    if (string.IsNullOrWhiteSpace(signingSecret))
        throw new InvalidOperationException("Resend webhook signing secret is required.");
    _ = NotificationMessageId.Create(Guid.NewGuid(), messageIdDomain);
    builder.Services.AddResendWebhooks(options => options.Secret = signingSecret);
}

var app = builder.Build();
app.MapExcelloreHealthChecks();
if (resendEnabled)
{
    app.MapPost("/webhooks/v1/resend", async (HttpRequest request,
        WebhookValidator validator, ProviderWebhookDatabase database,
        CancellationToken cancellationToken) =>
    {
        var context = await validator.ValidateAsync(request, cancellationToken);
        if (!context.IsValid)
            return Results.Problem(
                type: "urn:excellore:problem:invalid-webhook-signature",
                title: "Webhook verification failed.", statusCode: StatusCodes.Status400BadRequest);

        var status = context.Event?.EventType switch
        {
            WebhookEventType.EmailSent => "sent",
            WebhookEventType.EmailDelivered => "delivered",
            WebhookEventType.EmailDeliveryDelay => "delayed",
            WebhookEventType.EmailBounced => "bounced",
            WebhookEventType.EmailComplained => "complained",
            WebhookEventType.EmailFailed => "failed",
            WebhookEventType.EmailSuppressed => "suppressed",
            _ => null
        };
        if (status is null || context.Event is null || string.IsNullOrWhiteSpace(context.MessageId))
            return Results.NoContent();

        var data = context.Event.DataAs<EmailEventData>();
        if (data is null || !NotificationMessageId.TryParse(data.MessageId, messageIdDomain,
                out var correlationId))
            return Results.NoContent();

        await database.ApplyAsync(new ProviderDeliveryUpdate(
            "resend", context.MessageId, correlationId, data.EmailId, status,
            context.Event.MomentCreated), cancellationToken);
        return Results.NoContent();
    }).DisableAntiforgery();
}
app.Run();

public partial class Program;
