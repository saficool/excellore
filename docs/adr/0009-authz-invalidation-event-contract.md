# ADR-0009: Authorization-change event validation and cache invalidation

Status: proposed
Date: 2026-10-12
Owner: agent: needs human review

## Context

The handbook sections 7.9, 16.2, 16.3, and Appendix H require `access.authz.changed.v1` to invalidate cached authorization decisions. RabbitMQ delivery is at least once. A duplicate delivered after a fresh resolve should not unnecessarily discard that fresh decision. A malformed or cross-tenant event must not invalidate another tenant's entries. INV-02, INV-07, INV-10, INV-11, and INV-16 apply.

## Decision

`Excellore.Contracts` defines a C# envelope and an embedded JSON Schema for `access.authz.changed.v1`. The transport-neutral handler checks the event type, source, ID, actor, tenant ID in both envelope and data, and positive authorization version before touching the cache. It passes the event's `userIds` to the existing tenant-scoped invalidator. An absent, null, empty, or nil-ID user list clears the whole tenant. Unknown fields are ignored for forward compatibility.

The handler remembers up to 10,000 successfully handled event IDs in process memory. It records an ID only after invalidation succeeds, so a failed delivery can retry. An event ID forgotten after the bound is reached may trigger an extra invalidation; this cannot grant access. The later RabbitMQ consumer will still need its durable inbox for general event idempotency. No tokens or personal values are logged or placed in errors.

## Consequences

The schema is packaged with `Excellore.Contracts`, and tests cover targeted and tenant-wide invalidation, duplicate delivery, invalid events, and failed-delivery retry. Broker subscription, durable inbox, and Valkey invalidation remain for later WP-03 slices. Review this bounded in-memory behavior before connecting the live event stream.
