# ADR-0027: Tenant member lifecycle event contracts

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-03 requires contracts for `tenant.member.added.v1`, `suspended.v1`, `reactivated.v1`, and `removed.v1`. Access owns membership changes (INV-08); consumers must run in the envelope tenant context (INV-07) and suppress duplicate work (INV-16). RFC-0003 records the owner's decision to remove role names from the added event (INV-11, INV-13).

## Decision

Each event has its own C# payload record, Draft 2020-12 JSON Schema, and example. Parsers require the Access source, a nonempty envelope tenant ID equal to `data.tenantId`, and a nonempty opaque `userId`. The added event also requires a nonempty opaque `membershipId`. Known role-name fields are rejected in the added event. The payload has no role names, status text, or personal values. Unknown unrelated fields remain allowed for forward compatibility.

Consumers parse before entering tenant context and use the durable inbox keyed by envelope ID. The contracts do not confer tenant authorization on a worker or browser.

## Consequences

Profile and other consumers can seed a tenant member profile, mirror status, or remove tenant-scoped data in later work packages. A consumer that needs current role assignments must use the Access authority. The parser's nonempty identifier rules are proposed public contract choices for owner review.
