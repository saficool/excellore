# ADR-0074: Privacy-preserving tenant probing alert

Status: Proposed (WP-13 implementation for repository-owner review)

## Context

Handbook section 28 requires an alert when one actor produces many tenant-gate `404` responses. INV-11 prohibits personal data such as user IDs and IP addresses in logs, events, URLs, and errors. Exporting either identifier as a metric label would also create unbounded cardinality.

Section 28 previously listed `userId` as an operational log field. The handbook now follows INV-11 and keeps actor attribution in the restricted audit store required by INV-12.

## Decision

Each API detects repeated tenant-gate denials per authenticated user, or per remote address when no valid user ID is available, in process memory. It exports only an anonymous probing-alert counter. Actor keys are never logged, emitted, or attached to telemetry. The default threshold is 20 denials in five minutes with a five-minute alert cooldown.

Prometheus alerts on any increase of the anonymous counter. WP-14 ingress controls provide the deployment-wide rate limit and a second probing boundary across replicas.

## Consequences

The alert proves that repeated probing occurred without identifying the actor. Operators correlate the alert with access controls and restricted infrastructure telemetry under the incident process. A probe spread below the threshold across many replicas may evade this application detector until WP-14 adds the ingress boundary.

INV-02, INV-10, and INV-11 apply.
