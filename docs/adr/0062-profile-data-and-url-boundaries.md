# ADR-0062: Profile data and URL boundaries

Status: proposed
Date: 2026-10-16
Owner: agent; needs human security review

## Context

WP-09 adds global account data and tenant member profiles. The handbook's original member detail and admin routes put a user ID in a URL, and its avatar storage path included a user ID. Signed avatar URLs would expose a bearer secret in a URL. The owner approved request-body member contracts, opaque avatar keys, and authenticated BFF streaming. Profile also needs a hybrid audit table with forced RLS and a global outbox.

## Decision

Profile's member lookup uses `POST /v1/tenants/{tenantId}/members:lookup`; admin edits use `PATCH /v1/tenants/{tenantId}/members`. Both carry the target `userId` in the body and pass the shared tenant gate and permission check. Member profiles have forced RLS. The hybrid audit table has forced RLS and only a runtime INSERT policy: tenant entries require the transaction's `app.tenant_id`, and every entry requires `app.actor_sub`. The API role has no audit read, update, or delete grant. The global outbox has no `tenant_id` column and emits only global Profile events.

Avatar keys use opaque `avatars/{uploadId}.webp` paths. The WP-09 API accepts bounded image input, decodes and re-encodes it inline, stores only the processed WebP image, and serves it through the authenticated Profile BFF. The WP-10 worker may generate additional sizes. The browser never receives a signed object-store URL or credential.

## Consequences

Clients must use the new member contracts. Local and disposable PostgreSQL tests cover tenant RLS, active visible directory filtering, audit insertion, and event idempotency. Avatar tests cover content-type mismatch and re-encoding. The Profile worker will project membership events in WP-10; until then, an active member's row is created on their first gated Profile access. INV-02, INV-03, INV-04, INV-05, INV-10, INV-11, INV-12, and INV-16 apply.
