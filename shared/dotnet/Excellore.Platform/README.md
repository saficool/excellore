# Excellore.Platform

`TenantUnitOfWork<TDbContext>` runs database work in a transaction. It reads the tenant ID from the scoped `ITenantIdAccessor`, executes parameterized `SELECT set_config('app.tenant_id', ..., true)` as the first statement inside the transaction, then runs the work, saves changes, and commits. It rejects a missing tenant or a scope change before commit. Failed work rolls back when the transaction is disposed.

Register `AddExcelloreAuthz()` in the API so `ITenantIdAccessor` resolves to the same scoped tenant context opened by the tenant gate. Worker scopes must provide an authorized tenant accessor before using the unit of work. Use the same `DbContext` instance in the work delegate; never issue tenant database commands outside this boundary. See proposed ADR-0010.

The transaction tests use SQLite to verify statement order, commit, rollback, and scope checks.

`TenantDbContext` provides the EF defense in depth for class T entities. Map only `ITenantOwnedEntity` classes in a derived context; its sealed model-building step applies a filter using the current scoped tenant for every mapped class T entity. It rejects unsupported entity types and preexisting filters so a tenant filter is never silently omitted or overwritten. Synchronous and asynchronous `SaveChanges` stamp an empty `TenantId` on new rows and reject writes whose current or original tenant differs. Class P and H tables require separate, explicit context designs. This EF filter supplements PostgreSQL forced RLS; it does not replace it. See proposed ADR-0011.

The PostgreSQL RLS checks and a Testcontainers fixture are in `Excellore.Platform.Testing` (proposed ADR-0012). Each service must run the checks against its own migrations and restricted runtime role before exposing tenant tables.
