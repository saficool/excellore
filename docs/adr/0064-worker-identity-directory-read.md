# ADR-0064: Worker identity directory reads without person IDs in URLs

Status: proposed
Date: 2026-10-16
Owner: agent; owner approved the scoped Access and Profile worker grants

## Context

WP-10 needs nightly drift checks against Keycloak and Profile mirror refresh after `identity.user.updated.v1`. The event contains only an opaque user ID. Keycloak's per-user Admin API places that ID in a URL, contrary to INV-11. Existing local realms also do not automatically adopt changes to their imported service-account roles.

## Decision

The shared Keycloak directory reader uses `worker-access` or `worker-profile` client credentials and pages `/admin/realms/excellore/users` with only numeric pagination in the URL. It bounds each page to 100 users and 1 MiB and the whole scan to 10,000 users. The worker finds the target ID in memory and never logs the returned personal fields. Both machine service accounts receive only Keycloak `view-users`. A code-owned local reconciliation adds that role to existing development realms; its Admin API URL contains the machine service-account UUID, never a person's UUID.

Access's worker reads only `identities.user_id` and `identities.status` to count missing or mismatched Keycloak enabled flags. It reports only the count. Profile's worker updates only the existing global profile's mirrored names and locale after a valid, idempotent event, using a grant limited to its target and mirror columns. It does not modify a tenant profile or create a global profile for an unknown user. Failed directory reads leave the event unacknowledged for retry.

## Consequences

The directory scan is bounded and will fail closed above 10,000 users; paging or an identity-provider endpoint that accepts IDs in a body must replace it before that scale. Worker credentials and directory responses stay server-side. Disposable PostgreSQL tests cover grants, idempotency, and tenant isolation; a shared HTTP test asserts that person IDs and email never enter directory URLs. INV-03, INV-07, INV-11, INV-13, and INV-16 apply.
