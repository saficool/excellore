# ADR-0059: Tenant assignment list for the Access console

Status: proposed
Date: 2026-10-16
Owner: agent; needs human review

## Context

WP-08 requires the tenant console to show and revoke role assignments. The existing API can grant and revoke by assignment ID but cannot retrieve assignment IDs after a reload. A browser must not use a user identifier in a URL (INV-11), and all tenant reads must pass the Access tenant gate and forced RLS (INV-02, INV-03, INV-04, INV-16).

## Decision

Add `GET /v1/tenants/{tenantId}/role-assignments` under `access.assignment.manage`. It returns active direct role assignments, capped at 500, with assignment, user, and role IDs, role name, expiry, and creation time. The runtime query sets the tenant context inside a transaction and filters by the resolved tenant ID. The UI calls it through the shared BFF and sends a reason code in the body when revoking by assignment ID.

## Consequences

The console can revoke grants after reload. The new read is bounded and has a cross-tenant API test. A future paginated route may replace the 500-item view for larger tenants. This proposed API contract requires human review.
