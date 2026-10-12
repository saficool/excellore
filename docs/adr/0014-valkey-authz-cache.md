# ADR-0014: Versioned Valkey authorization snapshots

Status: proposed
Date: 2026-10-12
Owner: agent: needs human review

## Context

Handbook sections 7.9 and 20.8 require a 60-second Valkey cache beneath the 10-second in-process authorization cache. Authorization-change events can invalidate one user or a whole tenant. A fresh Access lookup may race with invalidation, so a delayed lookup must not restore an old decision. INV-02, INV-10, and INV-11 apply.

## Decision

`ValkeyAuthzCache` stores an authorization snapshot under an app-prefixed `authz:{tenantId}:{userId}` key with a Valkey-enforced 60-second TTL. It reads tenant and user generation counters alongside the snapshot in one atomic `MGET`. A Lua compare-and-set stores a fetched snapshot only if both generations still match. Invalidation increments the relevant generation: a missing, empty, or invalid user list increments the tenant generation; otherwise each named user's generation increments. Generation keys expire after five minutes, longer than every snapshot's lifetime. Old values remain physically until their 60-second TTL, but readers reject them immediately after an increment. The cache payload contains only the tenant ID, decision, statuses, and permission keys. The caller supplies a separate lowercase app prefix to keep application caches apart.

The cache is a transport-independent primitive. It is not registered as the resolver's source until the later WP-03 wiring step can propagate invalidations to every process's in-memory layer. Redis transport errors become a generic authorization-unavailable error without including keys or payloads. `StackExchange.Redis` 3.4.0 and `Testcontainers.Redis` 4.16.0 were checked as MIT-licensed packages at adoption.

## Consequences

The Valkey integration test proves TTL, cross-app and cross-tenant key separation, targeted and whole-tenant invalidation, and stale-write rejection. The generation check costs one `MGET` per Valkey read and retains small generation keys for five minutes. A future resolver integration must bypass Valkey safely on cache failure only by contacting Access, and must fan out invalidation to every process before this cache is used for live authorization decisions.
