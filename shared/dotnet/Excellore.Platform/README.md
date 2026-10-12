# Excellore.Platform

`TenantUnitOfWork<TDbContext>` runs database work in a transaction. It reads the tenant ID from the scoped `ITenantIdAccessor`, executes parameterized `SELECT set_config('app.tenant_id', ..., true)` as the first statement inside the transaction, then runs the work, saves changes, and commits. It rejects a missing tenant or a scope change before commit. Failed work rolls back when the transaction is disposed.

Register `AddExcelloreAuthz()` in the API so `ITenantIdAccessor` resolves to the same scoped tenant context opened by the tenant gate. Worker scopes must provide an authorized tenant accessor before using the unit of work. Use the same `DbContext` instance in the work delegate; never issue tenant database commands outside this boundary. See proposed ADR-0010.

This slice tests transaction behavior with SQLite. PostgreSQL RLS policies, EF tenant query filters and stamping, and the Testcontainers isolation harness remain WP-03 work; do not expose tenant tables until those checks pass.
