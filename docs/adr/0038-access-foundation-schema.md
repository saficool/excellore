# ADR-0038: Access foundation schema and tenant integrity

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-05 needs the Access schema from handbook Appendix F before the catalog, resolver, and tenant creation can be implemented. Its 20 tables include nine tenant tables and three hybrid tables. PostgreSQL foreign keys to globally keyed roles and groups do not by themselves prove that an assignment belongs to the same tenant. The generic development bootstrap also grants broad default table privileges to `access_app`.

## Decision

`001_initial.sql` creates the Appendix F schema as `access_migrator`. Every table has a P, H, or T comment; all H and T tables enable and force RLS. The migration explicitly revokes the bootstrap's broad table grants and future default grants, then grants the Access runtime role only the operations needed by the current schema. The local role bootstrap no longer restores broad Access defaults when `dev-up.sh` runs again; Profile retains its existing bootstrap grants. Audit rows are append-only; `outbox` is insert-only for the app role. A later publisher role will receive only outbox read and publication-state update rights.

`user_tenant_index` and `support_grants` are class P tenant-keyed metadata, so they join `outbox` in the reviewed RLS exemption file. Their access is restricted through grants and Access API policy, as specified by handbook section 8.2. Support grants are read-only to the runtime role until the WP-11 grant workflow is built.

Composite foreign keys bind group membership and group role assignments to their tenant. Before-insert/update triggers require role permission rows to use the role's tenant and require member/group assignments to use a system role or a role from the same tenant. The triggers run with the caller's RLS visibility and return a generic constraint error without exposing IDs.

## Consequences

INV-03, INV-08, INV-10, INV-11, INV-12, INV-14, and INV-16 apply. Disposable PostgreSQL tests run the migration as the non-superuser migrator, inspect all table classes and RLS flags, exercise every tenant table with two tenants, reject cross-tenant role and group references, and verify runtime grants. Later Access migrations must grant their own table privileges explicitly. The API must keep platform-scope operations behind platform policies and maintain `user_tenant_index` and outbox rows in the same transaction as membership or authorization changes. This proposed security decision needs owner review.
