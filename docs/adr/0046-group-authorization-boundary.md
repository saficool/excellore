# ADR-0046: Group authorization boundary and body-based membership routes

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-06 requires group data and API support before the later groups UI. Appendix I originally placed `userId` in group membership URLs, contrary to INV-11. A group member add may confer every active role assigned to that group, so checking only `access.group.manage` would bypass S1 and S3. A group role change can also change the actor's own roles when they belong to that group, contrary to S2. Group role assignments lacked a live-grant uniqueness constraint.

## Decision

Use `GET/POST /v1/tenants/{tenantId}/groups` for listing and creating groups. Use `PUT` and `DELETE /v1/tenants/{tenantId}/groups/{groupId}/members` with `userId` and a machine-safe reason code in the body. Use `POST /v1/tenants/{tenantId}/groups/{groupId}/role-assignments` with `roleId`, optional `expiresAt`, and a reason code; revoke through `DELETE /v1/tenants/{tenantId}/groups/{groupId}/role-assignments/{assignmentId}` with a reason code in the body. No user identifier appears in these URLs. Names are case-insensitively unique in the tenant, 3–60 characters, with an optional description up to 500 characters. Names and descriptions remain in Access storage and responses; audit and outbox records use opaque IDs and machine-safe reason codes.

Every mutation runs after the shared tenant gate, then takes the Access audit lock, opens the tenant RLS context, locks the tenant, and re-resolves the actor's current permissions. Adding a member evaluates each currently effective group role against the actor's permissions and MFA state. Granting or revoking a group role uses the same role policy as direct assignments; the ordinary API refuses owner-role changes. Removing a member from a group with an existing effective owner grant is also refused, so legacy data cannot bypass ownership transfer. A group member cannot change that group's role assignments, and no actor can add or remove themselves from a group. An expired group role grant can be replaced atomically. A partial unique index allows one live group-role grant per group and role. Mutations write the audit and `access.authz.changed.v1` outbox event in one transaction.

## Consequences

Clients send a JSON body for membership DELETE and role-assignment DELETE. This group does not add a separate group event type; the authorization invalidation contract already covers every permission-affecting change. The later expiry worker must still revoke expired group grants on schedule. Integration tests cover cross-tenant group and assignment IDs, member and role escalation, self-change restrictions, owner protection, MFA, expiry replacement, RLS, and audit/version changes. This proposed public-contract and security decision needs owner review. INV-02, INV-03, INV-04, INV-08, INV-10, INV-11, INV-12, and INV-16 apply.
