# ADR-0011: EF class T query filters and tenant stamping

Status: proposed
Date: 2026-10-12
Owner: agent: needs human review

## Context

Handbook section 8.4 requires a global query filter and `SaveChanges` tenant stamping for every class T entity as a second line of defense behind PostgreSQL forced RLS. A cached EF model must use the current context's tenant rather than capture the first request's tenant. An existing query filter must not be silently overwritten. INV-03, INV-10, and INV-16 apply.

## Decision

`TenantDbContext` is a class T-only base context. Derived contexts configure their model through `ConfigureTenantModel`; a sealed model-building step adds `TenantId == current scoped tenant` to each mapped `ITenantOwnedEntity`. Unsupported platform or hybrid entities, owned types, inheritance variants, and preexisting filters cause model construction to fail until explicitly designed. Both synchronous and asynchronous `SaveChanges` stamp an unset tenant ID on new rows and reject added, changed, or deleted rows whose current or original tenant ID differs from the scope. The production scoped accessor throws without an active context; an empty ID in a test accessor returns no rows and rejects writes.

## Consequences

The tests use two EF contexts sharing one SQLite database to catch tenant capture through EF model caching and to prove cross-tenant reads and writes fail at the application layer. Class P and H contexts need separate, explicit designs; the Access role and outbox models must not be forced into this class T base. `IgnoreQueryFilters` can bypass an EF filter, so PostgreSQL forced RLS and the Testcontainers isolation harness remain mandatory before tenant tables ship. Review the class T-only constraint before service adoption.
