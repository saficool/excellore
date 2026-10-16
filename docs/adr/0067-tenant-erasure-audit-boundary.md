# ADR-0067: Tenant erasure audit boundary (proposed)

## Context

WP-12 asks for zero active tenant rows after deletion. Handbook §22.3 also
requires append-only audit history, and Access has foreign keys from audit rows
to the live tenant registry. Deleting audit rows would break that history.

## Decision

The owner chose a restricted audit archive and a separate tombstone. Tenant
erasure must remove active tenant data and objects after the 14-day grace period.
The Access service records each service's confirmation and only finalizes after
both confirm. The finalization migration will copy tenant audit rows to a
restricted archive, preserve their hash chain, remove the live tenant record,
and leave an opaque tombstone and confirmation timestamps. No application
runtime role may list archived audit data.

Dispatch remains separate from finalization: the Access worker atomically
records the dispatch marker, machine audit, and `tenant.deleted.v1` outbox
event. A failed consumer therefore cannot be mistaken for completed erasure.

## Consequences and validation

The zero-row acceptance check covers active tenant tables in Access and
Profile; the restricted archive and tombstone are explicit exceptions. Tests
must count rows as the migrator, prove duplicate events are idempotent, and
prove a wrong tenant context cannot affect another tenant.

INV-03, INV-07, INV-11, INV-12, INV-16.
