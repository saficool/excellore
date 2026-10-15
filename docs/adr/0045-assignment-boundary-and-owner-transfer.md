# ADR-0045: Assignment routes and owner transfer boundary

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-06 needs direct role grant and revoke endpoints. Appendix I originally placed `userId` in the route, while INV-11 forbids personal data in URLs. The owner role also participates in the last-owner and accepted-transfer safeguards (S4 and S9). A direct assignment endpoint cannot safely substitute for that separate transfer flow. Time-bound grants can remain in the database after their effective expiry until the expiry worker processes them, while the active uniqueness index still blocks a second grant.

## Decision

Use `POST /v1/tenants/{tenantId}/role-assignments` with `userId`, `roleId`, optional `expiresAt`, and a required reason code in the body. Use `DELETE /v1/tenants/{tenantId}/role-assignments/{id}` with a required reason code in the body. Codes are lowercase, at most 64 characters, and contain only letters, digits, `.`, `_`, or `-`; they begin with a letter. The route never contains a user ID. The ordinary grant/revoke service refuses changes to the system `owner` role; ownership transfer will use its own accepted and audited flow. It refuses self-assignment and changes to a nonmember, checks that the actor currently holds every permission in the role, and requires recent MFA for privileged roles. A grant with an already expired timestamp is rejected. When an existing grant has expired but remains unrevoked, the service archives it and inserts the replacement in one transaction, with separate audit and informational events but one tenant authorization version increment.

The tenant gate runs before handlers. The write transaction takes the shared Access audit advisory lock, opens the tenant RLS context, locks the tenant, and re-resolves the actor's current permissions before changing any assignment. Allowed and denied decisions reached inside the service are audited. Events contain only opaque identifiers and optional expiry; reason codes remain in Access audit/storage.

## Consequences

Clients must send a JSON body on DELETE. An assignment ID from another tenant returns 404 under RLS. The owner transfer endpoint remains future WP-06 work. The later expiry worker still needs to revoke expired grants and invalidate caches on schedule; this service handles a direct regrant before the worker runs. The HTTP integration test checks cross-tenant IDs, self-assignment, owner protection, escalation, step-up, expiry replacement, event schemas, audit records, and authorization version changes. This proposed public-contract and security decision needs owner review. INV-02, INV-03, INV-04, INV-08, INV-10, INV-11, INV-12, and INV-16 apply.
