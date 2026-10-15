# ADR-0049: Member lifecycle authorization and body-based routes

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-06 requires suspending, reactivating, and removing tenant members without affecting global identity or other tenants. Appendix I previously placed `userId` in member mutation URLs, conflicting with INV-11. The owner chose body-based routes. Reactivating a suspended member may restore role and group grants that the acting admin could no longer grant. An owner status change can bypass last-owner protection or ownership controls if treated like an ordinary member change.

## Decision

Use `POST /v1/tenants/{tenantId}/members:suspend`, `POST /v1/tenants/{tenantId}/members:reactivate`, and `DELETE /v1/tenants/{tenantId}/members`, each with `userId` and a machine-safe reason code in the body. The shared tenant gate and `access.member.suspend` or `access.member.remove` run before the handler; the service rechecks current authority inside the transaction. Ordinary admin endpoints refuse self changes and support-grant memberships. Suspending preserves grants. Reactivation accepts only a suspended member with an active global identity and rechecks every effective direct and group role against the actor's current permissions and MFA. Owner status changes require `access.tenant.transfer` and recent MFA; suspending or removing an active owner also requires another active owner with an effective owner grant.

Removal marks the membership `removed`, revokes direct role assignments, and removes group memberships. All three transitions update the user-tenant index through its trigger, increment tenant authorization version, write an append-only audit entry, and enqueue the existing `tenant.member.<verb>.v1` and `access.authz.changed.v1` events in the same transaction. A repeated or invalid transition is denied and audited without another event.

## Consequences

The person retains their global identity and memberships in other tenants. A removed member rejoins by a new invitation, which creates fresh assignments. Clients send a JSON body with DELETE. Member events carry only opaque tenant and user IDs. This public contract and the owner-specific status rules need owner review. INV-02, INV-03, INV-04, INV-08, INV-10, INV-11, INV-12, INV-13, and INV-16 apply.
