# ADR-0072: Private cross-service tenant exports (proposed)

## Context

WP-12 requires an owner-requested tenant export containing Access and Profile
data. INV-09 prevents direct application API or database calls, and INV-11
prevents export content, object keys, or download credentials from appearing in
events or URLs.

## Decision

Access owns the request, status, final assembly, and authenticated download. It
publishes `tenant.export.requested.v1` with only `tenantId` and the opaque
`exportId`. Access and Profile independently read the requested tenant under
forced RLS and write JSON contributions to private S3-compatible storage.
Profile publishes `profile.tenant-export.ready.v1`, also carrying only those
two IDs. Access assembles the contributions, marks the job ready, writes the
security audit event, and streams the result through the tenant-gated Access
API.

The worker identity is the actor on the readiness event and final audit. Tenant
deletion removes the Access, Profile, and complete export objects before
deleting the job row. The one-export-per-tenant-per-UTC-day limit from
ADR-0069 remains in force.

## Consequences

The shared bucket is private staging infrastructure. It does not create a
synchronous application dependency. RabbitMQ messages remain small and safe to
inspect, retry, and dead-letter. Automatic object retention remains deferred
under Q-11; explicit tenant deletion removes associated objects.

INV-02, INV-03, INV-04, INV-07, INV-09, INV-11, INV-12, INV-16.
