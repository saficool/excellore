# ADR-0018: Transactional PostgreSQL outbox writer

Status: accepted by @saficool on 2026-10-14
Date: 2026-10-14
Owner: @saficool

## Context

Handbook section 16.4 requires each produced event to enter the outbox in the same transaction as its state change. Appendix F defines `outbox` as a class P, RLS-exempt table with a nullable tenant routing key. The repository's `infra/db/rls-exempt.txt` is still empty because service migrations have not created this table; a migration that adds it must add the exemption with security review. The shared library has a consumer and an inbox but no producer-side write primitive. INV-03, INV-07, INV-09, INV-11, and INV-16 apply.

## Decision

`PostgresEventOutbox.EnqueueAsync` receives the caller's active `DbTransaction` and inserts one event into the service's outbox table without opening or committing a separate transaction. The caller must set tenant context and commit the state change and event together. A duplicate event ID fails on the primary key rather than silently dropping an event. The payload must be UTF-8 JSON, at most 256 KiB, with CloudEvents `specversion`, `id`, `type`, and `tenantid` metadata matching the outbox row. A null tenant is allowed only when the envelope's `tenantid` is absent or null. The writer uses parameters and never logs payloads. Producers remain responsible for validating their event against its contract schema and excluding secrets and personal data.

Each service migration creates its own outbox table, adds the required exemption entry, and grants the runtime role only INSERT; a separate publisher role will need read and publish-state update grants. The shared writer does not create a table, publish to RabbitMQ, or mark rows as published. A publisher is a later WP-03 step.

## Consequences

A restricted PostgreSQL integration test proves commit and rollback atomicity with tenant data, duplicate-ID rejection, metadata validation, global-event routing, and cross-tenant RLS denial. The outbox may grow until a publisher processes rows; retention remains the handbook's Q-11 placeholder and no purge is introduced.
