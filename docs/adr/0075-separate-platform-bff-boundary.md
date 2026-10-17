# ADR-0075: Separate Platform BFF boundary

Status: Accepted by repository owner (2026-10-17)

## Context

The platform console was implemented as a `/platform` route group inside the tenant-facing Access Next.js application. Platform staff and tenant members therefore shared one OIDC client, session namespace, deployment, and web attack surface even though their authorization models differ.

The repository owner requires Platform to be a separate application. Tenant provisioning, owner invitations, app enablement, support grants, and authorization invalidation all mutate Access-owned records and must remain atomic under the Access service. A separate Platform API that writes the Access database would violate service data ownership. A pass-through Platform API would add a network and operational boundary without owning business logic. Moving the data to a new service would split the authorization authority and conflict with INV-08.

## Decision

Create `apps/platform` as a separate Next.js UI and BFF with its own host, confidential OIDC client, host-only session cookie, Valkey prefix, container, ingress, and deployment. Only `excellore-platform` receives scoped `platform_roles`; `excellore-access` no longer receives them.

The Platform BFF may synchronously call only the Access API's documented `/v1/platform/**` contracts. This is an owner-approved narrow exception to INV-09. Access remains the sole owner of tenants, invitations, memberships, authorization data, app enablement, support grants, audit writes, outbox publication, and authorization invalidation. Platform owns no database and contains no authorization business logic.

## Consequences

Platform staff and tenant users have isolated browser sessions, OIDC clients, routes, and deployments. Compromise or deployment failure of the tenant-facing Access UI does not directly expose the Platform UI session boundary. Access continues to enforce every `Platform.*` policy and remains the only service that writes its database.

The Platform app depends synchronously on the Access API and is unavailable when Access is unavailable. A future Platform service is justified only when it owns an independent business domain and database, such as billing or commercial plan management; it must not take ownership of tenant authorization data.

INV-05, INV-06, INV-08, INV-09, INV-11, INV-12, INV-14, and INV-18 apply.
