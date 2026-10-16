# ADR-0071: Private cross-service personal exports (proposed)

## Context

WP-12 requires one personal-data download containing data from Access and
Profile. INV-09 requires event-based inter-app communication, while INV-11
forbids personal data in events and URLs. Export payloads are too large and
too sensitive for RabbitMQ messages.

## Decision

Profile owns the request, final assembly, status, and authenticated download.
It publishes `profile.export.requested.v1` with only opaque `userId` and
`exportId`. Access and Profile independently build user-bounded JSON parts in
a private S3-compatible bucket under a deterministic key derived from the
opaque export ID. Access publishes `access.user-export.ready.v1`, also IDs
only. Profile reads both parts, assembles the final object, marks the job ready
for 24 hours, and publishes the existing `profile.export.ready.v1` event.

Worker database grants remain column or purpose limited. Profile tenant rows
use a forced-RLS policy bound to a transaction-local export user ID. Browsers
receive the final object only through the authenticated Profile API; no signed
URL or storage key is returned.

## Consequences

The private export bucket is shared staging infrastructure, not an app API or
database. Services communicate readiness only through events. Object contents
can contain personal data and therefore require encryption, private access,
and lifecycle cleanup in deployment infrastructure. Automatic retention purge
remains deferred under Q-11; the API enforces the 24-hour download window.

INV-02, INV-03, INV-05, INV-07, INV-09, INV-11, INV-16.
