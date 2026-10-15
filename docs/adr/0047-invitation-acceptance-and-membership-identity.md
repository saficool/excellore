# ADR-0047: Invitation acceptance and membership identity

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-06 requires single-use, 256-bit invitation secrets, verified-email equality, tenant isolation, and a `tenant.member.added.v1` event carrying `membershipId`. The initial `tenant_members` table has no membership identifier. An invitation can remain pending while its inviter loses access or its selected roles change. Accepting a stale grant would violate S1 and INV-02. RFC-0006 records the owner's decision to keep the invitation secret out of the URL.

## Decision

Add a generated, unique, opaque `membership_id` to class T `tenant_members` in a backward-compatible migration. Acceptance reads the tenant ID from the one-time code in the request body only to open the matching RLS context, then verifies the invitation ID and stored SHA-256 hash, pending status, expiry, verified Keycloak email mirror, and exact invited-email match. A transaction locks the invitation, validates the current tenant and role/group references, creates or reactivates the membership, records grants, marks the invitation accepted, writes the audit event and authorization and member events, and commits. For active tenants, the inviter must still hold `access.member.invite` and must still be able to grant every selected role and every active group role. Owner invitations are accepted only while the tenant is provisioning. Replayed, expired, wrong-email, and cross-tenant codes give no access. Audit and events contain opaque IDs and safe reason codes only.

The tenant invitation listing and revocation routes use the shared tenant gate and `access.member.invite`; revocation is audited. The authenticated user's pending-invitation list reads only their current verified email from the identity mirror, then checks each active or provisioning tenant under its own transaction-local RLS context. This avoids an RLS-exempt email index; a later scale review may replace the per-tenant scan with a narrowly scoped index. Issuance, resend, Keycloak creation, and email delivery are specified in ADR-0048.

## Consequences

Historical memberships receive generated IDs; no existing caller must provide one. Acceptance fails closed if a selected role or group changes incompatibly before acceptance. The proposed migration and public acceptance behavior need owner review. INV-02, INV-03, INV-04, INV-08, INV-10, INV-11, INV-12, INV-13, and INV-16 apply.
