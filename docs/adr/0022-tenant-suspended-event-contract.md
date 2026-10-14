# ADR-0022: Tenant suspension event contract

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

Handbook sections 16.2, 16.3, and Appendix H require `tenant.suspended.v1` so services can stop tenant work after Access suspends a tenant. The event is tenant-scoped, uses at-least-once delivery, and must carry only the tenant ID and a reason code. INV-07, INV-09, INV-10, INV-11, and INV-16 apply.

## Decision

`Excellore.Contracts` defines a typed `TenantSuspendedV1` payload and an embedded Draft 2020-12 JSON Schema. Both envelope and data must contain the same nonempty tenant ID. The reason code is a 1–64 character lowercase ASCII identifier using letters, digits, `_`, or `-`; there is no free-text reason field. Producers must choose codes that disclose no personal data. The parser rejects malformed or oversized events with a fixed error message. Unknown fields remain allowed for forward compatibility. The existing authorization-change event uses the same internal tenant-envelope parser, keeping its validation behavior.

A consumer parses the event before using its tenant ID, then uses the durable inbox and tenant-scoped database transaction. A duplicate event ID does not run work again. The event does not authorize a user; a worker uses its machine identity and rechecks resource ownership within the tenant context.

## Consequences

The contract is ready for Access to publish and for service workers to consume in later work packages. The contract does not implement tenant suspension or stop jobs by itself. The reason-code syntax is a proposed public contract decision for owner review before producers rely on it. Tests cover schema validity, tenant mismatch, malformed reason codes, and duplicate consumer delivery through the PostgreSQL inbox.
