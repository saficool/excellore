# ADR-0007: Fail closed after local authorization cache expiry

Status: proposed
Date: 2026-10-12
Owner: agent: needs human review

## Context

The handbook sections 7.8 and 20.8 allow reads to use a cached authorization decision for up to five minutes when Access is unavailable, while writes must fail closed. The complete resolver will include a 60-second Valkey layer. This WP-03 slice implements only the required in-memory layer, whose maximum lifetime is 10 seconds. INV-02 and INV-10 require decisions to remain scoped to one user and tenant and to fail closed when current authorization cannot be established.

## Decision

Cache allow and deny decisions for at most 10 seconds per `(tenantId, userId)`. On expiry, fetch a new decision. If the source is unavailable, return `authz-unavailable`; do not serve the expired entry for reads or writes. Invalidation removes targeted users or the whole tenant. An empty or malformed user list clears the whole tenant. A fetch started before an invalidation is discarded and retried before being returned or cached.

## Consequences

Authorization changes take effect on invalidation or within 10 seconds. Reads lose availability when Access is down beyond that window until the Valkey layer and optional bounded read fallback are implemented. The short-lived cache is bounded to 10,000 entries per process. Review the outage behavior before exposing tenant endpoints in WP-05.
