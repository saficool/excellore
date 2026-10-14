# ADR-0025: Tenant lifecycle event contracts

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-03 requires typed event contracts for the section 16.3 catalog. Appendix H defines the tenant reactivation, deletion requested, and deletion payloads. The tenant must be present and identical in the envelope and payload (INV-07). These events can stop jobs or trigger erasure, so malformed or replayed messages must not run tenant work (INV-03, INV-11, INV-16).

## Decision

`tenant.reactivated.v1`, `tenant.deletion.requested.v1`, and `tenant.deleted.v1` are separate C# records and Draft 2020-12 JSON Schemas in `Excellore.Contracts`. They use the existing Access source and tenant envelope parser. The reactivation reason is a bounded machine-safe code, matching `tenant.suspended.v1`; free-text reasons are rejected. A deletion request requires a `dueAt` later than event `time`. The parser does not calculate or authorize the 14-day grace period; the Access producer must apply the section 36 default before publishing. All payloads require a matching nonempty tenant ID. Unknown unrelated fields remain allowed for forward compatibility.

Consumers parse before using the tenant ID and use the durable inbox and tenant-scoped transaction. Tests cover malformed payloads, tenant mismatch, cross-tenant isolation, and duplicate delivery for every new event.

## Consequences

These shared contracts prepare later Access and worker work packages; they do not publish events or perform deletion. Workers must not treat a parsed deletion event as authority to access another tenant, and must confirm erasure as specified in the handbook. The due-date validation and reason-code vocabulary are proposed public contract choices for owner review.
