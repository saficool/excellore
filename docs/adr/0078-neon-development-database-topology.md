# ADR-0078: Neon development database topology

Status: accepted; owner approved 2026-10-17

## Context

The local runtime used four persistent PostgreSQL containers. The owner wants the development
runtime moved completely to four Neon projects while keeping local Docker for the rest of the
platform. Excellore relies on forced RLS, transaction-local tenant context, triggers, PL/pgSQL,
advisory locks, and tightly scoped runtime roles. Neon transaction pooling cannot safely carry a
session-level advisory lock across commands.

## Decision

Use one Neon development project for each of Keycloak, Access, Profile, and Notifications. Keep the
database name configurable because a new Neon project commonly starts with `neondb`.

Create Excellore service roles through checked-in SQL. Runtime roles are login roles with no
superuser, database creation, role creation, inheritance, replication, `BYPASSRLS`, object
ownership, or administrative role membership. Project owner credentials are used only by the
explicit bootstrap command and are removed from the normal runtime environment afterward.

Use direct endpoints for Keycloak, migrations, bootstrap, and the Access worker. The Access worker
requires a stable session for its scheduler advisory locks. Use transaction-pooled endpoints for
the Access, Profile, and Notifications request workloads, outbox publishers, Profile worker, and
Notifications worker. Bound client pools and require TLS certificate and hostname verification plus
channel binding on every connection.

Keep PostgreSQL containers behind the `local-database-drill` Compose profile for disposable
recovery rehearsal only. Automated integration tests continue to use disposable PostgreSQL 16
Testcontainers. Destructive tests and restore rehearsals never target the persistent Neon projects.
The production cloud and production database topology remain undecided.

## Consequences

Local application state survives Docker resets and each service retains a separate database blast
radius. Normal startup now depends on network access and Neon availability, and a sleeping compute
may add cold-start latency. Database bootstrap is an explicit privileged operation. Recovery drills
and integration tests remain local and disposable, so they cannot erase development data.

INV-03, INV-07, INV-11, INV-14, and INV-16 apply.
