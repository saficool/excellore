using Excellore.Authz;
using Excellore.Notifications;
using Excellore.Notifications.Application;
using Excellore.Notifications.Infrastructure;
using Excellore.Notifications.Worker;
using Excellore.Platform;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("NotificationsWorker")
    ?? throw new InvalidOperationException("ConnectionStrings:NotificationsWorker is required.");
if (!Uri.TryCreate(builder.Configuration["Messaging:RabbitMqUri"], UriKind.Absolute,
        out var brokerUri))
    throw new InvalidOperationException("Messaging:RabbitMqUri is required.");
var allowLocal = builder.Environment.IsDevelopment() &&
    string.Equals(builder.Configuration["EXCELLORE_INSECURE_LOCAL_DEV"], "true",
        StringComparison.OrdinalIgnoreCase);
var smtpSecurity = Enum.TryParse<SmtpSecurity>(builder.Configuration["Email:SmtpSecurity"], true,
    out var configuredSecurity) ? configuredSecurity : SmtpSecurity.None;
var smtpOptions = new SmtpEmailOptions(
    builder.Configuration["Email:SmtpHost"],
    int.TryParse(builder.Configuration["Email:SmtpPort"], out var smtpPort) ? smtpPort : 0,
    builder.Configuration["Email:SmtpUsername"], builder.Configuration["Email:SmtpPassword"],
    builder.Configuration["Email:FromAddress"],
    builder.Configuration["Email:FromName"] ?? "Excellore",
    smtpSecurity,
    int.TryParse(builder.Configuration["Email:SmtpTimeoutSeconds"], out var timeout)
        ? timeout : 15);

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PostgresEventInbox>();
builder.Services.AddSingleton<NotificationDatabase>();
builder.Services.AddSingleton<IEventBus>(new RabbitMqEventBus(brokerUri));
builder.Configuration["Authz:ClientSecret"] ??=
    builder.Configuration["SVC_NOTIFICATIONS_AUTHZ_SECRET"];
builder.Services.AddAccessAuthzClient(builder.Configuration, allowLocal);
builder.Services.AddSingleton<INotificationContactResolver, HttpNotificationContactResolver>();
builder.Services.AddSingleton<INotificationTemplateRenderer>(_ => new NotificationTemplateRenderer(
    builder.Configuration["Notification:ShellOrigin"], allowLocal));
builder.Services.AddSingleton<IEmailSender>(_ => new MailKitEmailSender(smtpOptions, allowLocal));
builder.Services.AddSingleton<NotificationEventProcessor>();
builder.Services.AddHostedService<NotificationSubscriber>();

builder.Build().Run();
