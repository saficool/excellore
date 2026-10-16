# ADR-0069: Tenant export request limit (proposed)

## Context

WP-12 requires a per-plan tenant export limit but provides no numeric values.
The owner selected one export per tenant per day for the first release.
Exports contain sensitive cross-service data and must have an auditable request
and authenticated download, with no signed URL.

## Decision

The Access authority accepts owner requests after a fresh MFA check. A
tenant-scoped, forced-RLS `tenant_exports` job table has a unique key on
`(tenant_id, requested_day)`, where the day is UTC. The database, rather than
an API-side count, enforces the limit across concurrent requests. All plans
use the same first-release limit. Access and Profile will contribute data via
private storage and opaque-ID events; a job is downloadable only after both
contributions are complete.

## Consequences

The limit resets at UTC midnight. Failed requests still count toward the
day's limit. The owner sees a stable `429` problem response after reaching it.
The export remains pending until the cross-service worker is deployed.

INV-02, INV-03, INV-04, INV-07, INV-11, INV-12, INV-16, INV-17.
