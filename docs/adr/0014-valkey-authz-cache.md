# ADR-0014: Versioned Valkey authorization snapshots

Status: proposed
Date: 2026-10-12
Owner: agent: needs human review

## Context

Handbook sections 7.9 and 20.8 require a 60-second Valkey cache beneath the 10-second in-process authorization cache. Authorization-change events can invalidate one user or a whole tenant. A fresh Access lookup may race with invalidation, so a delayed lookup must not restore an old decision. INV-02, INV-10, and INV-11 apply.

## Decision

`ValkeyAuthzCache` stores an authorization snapshot under an app-prefixed `authz:{tenantId}:{userId}` key with a Valkey-enforced 60-second TTL. It reads tenant and user generation counters alongside the snapshot in one atomic `MGET`. A Lua compare-and-set stores a fetched snapshot only if both generations still match. Invalidation increments the relevant generation: a missing, empty, or invalid user list increments the tenant generation; otherwise each named user's generation increments. Generation keys expire after five minutes, longer than every snapshot's lifetime. Old values remain physically until their 60-second TTL, but readers reject them immediately after an increment. The cache payload contains only the tenant ID, decision, statuses, and permission keys. The caller supplies a separate lowercase app prefix to keep application caches apart.

`ValkeyAuthzSnapshotSource` composes the cache with the Access source. It returns a valid cache hit; on a miss it fetches Access and stores only if the generation is unchanged. A failed compare-and-set retries the lookup, bounded to three attempts, then fails closed. If Valkey is unavailable, it returns only a fresh Access decision; if Access is unavailable too, it fails closed. Redis transport errors become a generic authorization-unavailable error without including keys or payloads. `StackExchange.Redis` 3.4.0 and `Testcontainers.Redis` 4.16.0 were checked as MIT-licensed packages at adoption.

`ValkeyAuthzInvalidationFanout` starts an app-scoped Valkey pub/sub subscription on each replica. The event handler awaits local invalidation, distributed generation increment, and tenant-only publication before remembering the event ID. Subscribers clear the whole named tenant from their local 10-second caches, even when the distributed event targets one user; the shared cache remains targeted. If the subscriber has not started, Valkey fails, or no subscriber receives publication, the handler fails and broker delivery can retry. Pub/sub is ephemeral, so the 10-second local TTL is the backstop for a missed notification. No user ID, permission, decision, or event actor is published on the channel.

`AddValkeyAuthzResolver` provides one shared registration path for the Access HTTP source, Valkey cache, local resolver, fanout invalidator, event handler, and hosted subscriber. It requires an app-registered singleton `IConnectionMultiplexer` and a lowercase app prefix. Its `IHostedService` must start before any RabbitMQ consumer acknowledges authorization-change events. It is not called by the starter APIs until the broker consumer exists, so a Valkey snapshot cannot become a live decision source without event delivery. Repeated calls to the older cache or Access source registration do not silently replace this composition.

## Consequences

The Valkey integration tests prove TTL, cross-app and cross-tenant key separation, targeted and whole-tenant invalidation, stale-write rejection, cache-hit sharing between source instances, Access fallback after connection loss, cross-replica local invalidation, duplicate event handling, and the DI composition. The generation check costs one `MGET` per Valkey read and retains small generation keys for five minutes. The RabbitMQ consumer (ADR-0015) starts after the subscriber and acknowledges only after invalidation succeeds; app startup integration remains WP-03 work.
