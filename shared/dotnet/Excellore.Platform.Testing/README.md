# Excellore.Platform.Testing

Use `RlsDatabaseChecks` from each service's PostgreSQL isolation tests. Open the connection with that service's restricted `<app>_app` credentials, then assert `GetRuntimeRoleStatusAsync(...).IsSafe` and an empty result from `FindUnprotectedTenantTablesAsync`. Pass the reviewed entries from `infra/db/rls-exempt.txt` through `ParseExemptions`; do not add an exemption in a test alone.

The checks inspect every visible `public` table with a `tenant_id` column for both enabled and forced RLS, and verify that the runtime role is neither a superuser, a `BYPASSRLS` role, nor an owner of a public table (including ownership inherited through role membership). The service's test fixture must also exercise its class T tables with two tenants and the real runtime role. The platform test suite contains a working Testcontainers PostgreSQL example covering no context, cross-tenant reads and writes, transaction-local context reset, and a deliberately unprotected table.

Run `dotnet test Excellore.sln` with Docker available. The integration test uses a disposable PostgreSQL 16.12 container and generated credentials.
