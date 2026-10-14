# ADR-0037: Generated .NET service baseline

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-04 requires `dotnet new excellore-service` to generate an API, worker, and tests that use the shared tenant gate and PostgreSQL isolation harness. New services need one consistent starting point for authorization, persistence, events, and health checks.

## Decision

The template generates a sample class T item and a single read endpoint. The route uses `AddTenantGate`, `RequirePermission`, `TenantUnitOfWork`, and the shared class T EF context. It registers the Access resolver and invalidation consumer, so authorization fails closed until Access and its machine client are configured. The worker publishes outbox rows with a dedicated restricted publisher role. Inbox and outbox tables are class P infrastructure. The outbox's nullable tenant routing key is exempted from RLS under ADR-0018 and `infra/db/rls-exempt.txt`; the application role can only insert into it. Tests apply the generated migration in disposable PostgreSQL and prove forced RLS and cross-tenant denial with the real restricted runtime role.

The template uses the PostgreSQL licensed Npgsql EF Core provider and Apache 2.0 licensed OpenTelemetry packages. API readiness checks database connectivity without exposing exception details. Service-specific events and consumers require explicit contract schemas, idempotency tests, and reviewed registration when added.

## Consequences

INV-02, INV-03, INV-04, INV-05, INV-07, INV-08, INV-10, INV-11, and INV-16 shape the generated baseline. An app must provision its Keycloak clients, Access catalog, database roles, and configuration before starting the API. The worker needs a distinct publisher connection string. The sample route is not an app feature and should be replaced as a service is implemented. This proposed security decision needs owner review.
