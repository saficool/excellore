# Excellore .NET service template

Install and generate from the repository root:

```sh
dotnet new install ./templates/excellore-service
dotnet new excellore-service --name Excellore.Reports --appSlug reports --output services/reports
dotnet sln Excellore.sln add services/reports/Excellore.Reports.Api/Excellore.Reports.Api.csproj services/reports/Excellore.Reports.Worker/Excellore.Reports.Worker.csproj services/reports/Excellore.Reports.Domain/Excellore.Reports.Domain.csproj services/reports/Excellore.Reports.Infrastructure/Excellore.Reports.Infrastructure.csproj services/reports/Excellore.Reports.Tests/Excellore.Reports.Tests.csproj
dotnet build Excellore.sln
dotnet test services/reports/Excellore.Reports.Tests/Excellore.Reports.Tests.csproj
```

Generate only inside `services/<app>` so the shared project references resolve. Use a lowercase ASCII app slug that matches `api-<app>`, the Keycloak confidential clients, and the permission catalog. Provision `<app>_migrator`, `<app>_app`, and a dedicated `<app>_publisher` role with distinct credentials. Apply `infra/db/001_initial.sql` as migrator after creating those roles. The migration classifies `sample_items` as T and forces RLS. `inbox` and `outbox` are class P; the outbox's routing `tenant_id` is recorded in the reviewed root `infra/db/rls-exempt.txt`.

Configure `Auth:Authority`, `Authz:AccessInternalUrl`, `Authz:TokenEndpoint`, `Authz:ClientId`, `Authz:ClientSecret`, `Valkey:ConnectionString`, `Events:BrokerUri`, and `ConnectionStrings:App` for the API. Configure `Events:BrokerUri` and `ConnectionStrings:Publisher` for the worker. Keep secrets in ignored local files or a secret store. The publisher role needs only `SELECT` on `outbox` and `UPDATE` on `published_at`. The worker's tenant jobs must use `TenantWorkerScope`; validate event contracts and process them with `PostgresEventInbox` before acknowledging. The sample worker publishes outbox rows; add event subscriptions for actual app events.

The API exposes `/health/live` and `/health/ready`; readiness checks the app database. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export shared platform spans to an OTLP collector. No URL or message body is added to those spans.

The example API route uses the shared tenant gate and Access authority, then reads through `TenantUnitOfWork` and the class T EF context. It fails closed until Access is configured. Add each new tenant route to the API isolation harness and mark it in OpenAPI. Replace sample items and permissions with the app's reviewed contract; keep the security wiring in the shared libraries. The template's database test requires Docker and verifies the restricted role, forced RLS, and cross-tenant denial.
