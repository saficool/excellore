# ADR-0016: Transactional PostgreSQL event inbox

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

Handbook sections 16.4 and 21.3 require at-least-once consumers to deduplicate events by envelope `id` in an inbox table. Appendix F classifies `inbox(event_id, received_at)` as class P without a `tenant_id` column. A marker committed before the handler's database work could suppress a redelivery after that work fails. Tenant handlers also need transaction-local RLS context. INV-03, INV-07, INV-10, INV-11, and INV-16 apply.

## Decision

`PostgresEventInbox.RunOnceAsync` opens one database transaction, sets `app.tenant_id` transaction-locally, inserts the event ID with `ON CONFLICT DO NOTHING`, and invokes the handler only for a new ID. It commits the marker and handler's database work together. An exception before commit rolls both back. If the commit result is uncertain after a connection failure, broker redelivery reads the inbox marker and safely decides whether to run the work. For global events, it sets an empty tenant context so a pooled connection cannot inherit a tenant setting. An empty event ID or tenant ID is rejected. The handler receives the same `DbTransaction` and must use it for all database changes; external effects must be emitted through an outbox, not performed directly in the callback.

The inbox table is class P with `event_id uuid PRIMARY KEY` and `received_at timestamptz NOT NULL DEFAULT now()`. It has no `tenant_id` column and needs no RLS exemption entry. Each service migration will create this table and grant only the operations needed by its runtime role. The shared library does not create tables at runtime. The authorization-change cache handler remains idempotent in memory and does not need a database inbox because it makes no service database changes.

## Consequences

A Testcontainers PostgreSQL test proves one-time execution, rollback after handler failure, and RLS denial of cross-tenant reads and writes with a restricted non-owner role. The helper is a transactional primitive for later service consumers; broker acknowledgement must still occur only after `RunOnceAsync` returns successfully. This adds one inbox row per processed event, so service operations will need monitoring and retention decisions later. Retention remains Q-11's placeholder; no purge is implemented.
