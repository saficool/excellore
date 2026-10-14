# ADR-0023: Shared tenant API isolation checks

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

Handbook section 25.2 requires every service to run an API isolation matrix for each OpenAPI operation marked `x-excellore-tenant-scoped: true`. WP-03 already has PostgreSQL RLS checks but no reusable API matrix runner. INV-02, INV-03, INV-04, INV-10, and INV-16 apply.

## Decision

`Excellore.Platform.Testing` enumerates tenant-scoped OpenAPI operations, rejects a `{tenantId}` route without the marker (or a marker without the route parameter), and requires one probe per operation. The probe's callback sends a fresh request through the service test host for each scenario. The shared runner checks the expected status without reading response bodies: `404` for non-members, other-tenant members, foreign resources, unknown or suspended tenants, and removed members; `403` for an active member lacking permission.

An authorized request must also succeed, preventing an always-deny route from passing. Resource checks can be omitted only when the operation has no resource reference; the helper rejects that omission when the path has another parameter. The permission check can be omitted only for an intentional `AllowAnyMember` route. Both omissions are explicit probe fields and remain visible in service tests. A route with no marked tenant operation fails instead of silently passing. The runtime database-role and forced-RLS checks remain in `RlsDatabaseChecks` and must run alongside the API matrix.

## Consequences

Future service suites can use one shared scenario runner while supplying their real authenticated clients, tenant fixtures, resource IDs, and request bodies. The helper does not create those fixtures or assert that a callback uses the supplied identities correctly; each service suite must review and test its probe construction. The current starter services have no tenant API operations, so integration with real services begins in WP-05 and WP-09. No production route or authorization behavior changes in this step.
