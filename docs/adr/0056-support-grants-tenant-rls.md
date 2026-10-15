# ADR-0056: Support grants are tenant-scoped records

Status: Proposed (owner direction recorded 2026-10-15)

## Context

The initial schema made `support_grants` a platform table exempt from RLS. Automatic approval review rejected giving the runtime role write access to that exempt table. The owner chose to reclassify it under forced tenant RLS.

## Decision

Migration 011 makes `support_grants` class T, forces RLS, and grants runtime INSERT/UPDATE only after RLS is active. A `platform-support` staff member can request only their own `viewer` or `auditor` grant for at most eight hours with MFA. A different tenant owner with MFA approves it. Access then creates a support-flagged membership and assignment in one transaction. Resolution denies the member if the grant is inactive or expired, and ignores every assignment except the role bound to that grant. Support resolution currently permits only Access read permissions and the Access app; cross-app support awaits shared grant usage auditing. An owner can revoke the grant. Requests, approvals and revocations are audited. Each Access tenant request under a grant writes a usage audit record with the grant ID and route pattern before its handler runs. Authorization changes publish the existing invalidation event; approval and revocation publish the support grant events. Break-glass access remains unavailable until a separately reviewed dual-approval design exists.

## Consequences

The grant table leaves `rls-exempt.txt`. Platform requests set transaction-local tenant context solely for a requested row; they do not grant tenant data access. Expired rows remain for the later expiry worker, but authorization fails at expiry without waiting for that worker. INV-02, INV-03, INV-04, INV-08, INV-10, INV-11, INV-12, INV-16, and INV-18 apply. Security review is required before rollout.
