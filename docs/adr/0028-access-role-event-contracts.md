# ADR-0028: Access role event contracts

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-03 requires `access.role.created.v1`, `updated.v1`, and `deleted.v1`. RFC-0004 records the owner's decision to remove custom names and permission lists. Access is the sole authorization authority (INV-02, INV-08); tenant events must carry a tenant ID (INV-07), no personal values (INV-11), and idempotent consumers (INV-16).

## Decision

The three events have distinct C# payload records, Draft 2020-12 JSON Schemas, and examples. They require nonempty, matching envelope and payload tenant IDs and a nonempty opaque role ID. Parsers and schemas reject known `name`, `description`, and `permissions` payload fields while allowing unrelated unknown fields for forward compatibility. These contracts describe tenant-scoped custom role changes; system role templates remain code-first and do not use a tenant role event.

Consumers parse before tenant work and use the durable inbox keyed by envelope ID. An event is informational and cannot authorize a user or replace `access.authz.changed.v1` invalidation.

## Consequences

Consumers that need current role data make an authorized Access request. The nonempty ID rules and explicit forbidden-field list are proposed public contract details for owner review.
