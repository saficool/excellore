# ADR-0012: PostgreSQL RLS isolation harness

Status: proposed
Date: 2026-10-12
Owner: agent: needs human review

## Context

Handbook sections 8.3 and 25.2 require database enforcement to complement the EF tenant guard. An EF filter can be bypassed, and a PostgreSQL owner or privileged role can bypass an otherwise correct row policy. Every service needs the same metadata and role checks. INV-03 and INV-16 apply.

## Decision

`Excellore.Platform.Testing` provides checks for each service to run through its restricted runtime database connection. The metadata check finds every visible `public` table with `tenant_id` but without both `ENABLE` and `FORCE ROW LEVEL SECURITY`, skipping only entries in the reviewed `infra/db/rls-exempt.txt`. The role check rejects superuser, `BYPASSRLS`, and effective table ownership. A Testcontainers PostgreSQL fixture demonstrates the required class T policy with T1/T2 rows, no-context invisibility, cross-tenant read and write denial, and transaction-local context reset. The fixture first proves that the metadata check detects missing RLS and missing `FORCE`.

## Consequences

Each service must include this harness when it adds tenant tables, using its own migrations and runtime role, then add per-table T1/T2 cases. The shared fixture does not certify future service schemas automatically. CI needs Docker for `dotnet test`; the test uses generated credentials and a disposable database. No production schema or exemption list changes in this step.
