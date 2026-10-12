# ADR-0010: Transaction-local tenant database context

Status: proposed
Date: 2026-10-12
Owner: agent: needs human review

## Context

Handbook sections 8.3, 8.4, and 20.3 require every tenant database operation to run inside a transaction after setting `app.tenant_id` locally. A session-level setting could leak through pooled PostgreSQL connections. `Excellore.Authz` already owns the request gate's scoped tenant context, while `Excellore.Platform` must own the database unit of work. INV-02, INV-03, INV-04, INV-10, and INV-16 apply.

## Decision

`Excellore.Platform` defines a read-only `ITenantIdAccessor` and `TenantUnitOfWork<TDbContext>`. `AddExcelloreAuthz()` registers the existing gate context as that accessor, so database code receives the same scoped tenant ID that passed authorization. The unit of work begins a transaction, executes parameterized `SELECT set_config('app.tenant_id', tenantId, true)` before invoking application code, calls `SaveChangesAsync`, and commits. It refuses an empty tenant ID and rolls back if work throws or the scoped tenant ID changes before commit. The SQL never sets a session-level value.

## Consequences

The tests use SQLite with a test-only `set_config` function to prove call order, transaction commit, rollback, and scope checks without a running Docker daemon. They do not prove PostgreSQL RLS behavior. The EF query filter, `SaveChanges` tenant stamping, request filter, worker scope, and Testcontainers PostgreSQL isolation harness remain required WP-03 work. No tenant endpoint or table should ship until those protections and their integration tests are in place. Review this boundary before service adoption.
