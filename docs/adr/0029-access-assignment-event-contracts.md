# ADR-0029: Access assignment event contracts

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-03 requires `access.assignment.granted.v1` and `access.assignment.revoked.v1` contracts. Appendix H specifies opaque `tenantId`, `userId`, and `roleId` plus optional `expiresAt`. Access alone owns assignment state (INV-08), and event payloads cannot become an authorization source (INV-02). Events must carry tenant context, omit personal values, and support idempotent consumers (INV-07, INV-11, INV-16).

## Decision

Both events have typed C# records, Draft 2020-12 JSON Schemas, and examples. Parsers require nonempty IDs, with payload `tenantId` equal to envelope `tenantid`. `expiresAt` may be absent or a timestamp with an offset; explicit null is invalid. Consumers do not reject a past expiry because delivery can be delayed. Known free-form or authorization-bearing fields (`name`, `description`, `roleName`, `email`, `reason`, `permissions`) are rejected; unrelated unknown fields are allowed for forward compatibility.

Consumers parse before tenant work and use the durable inbox keyed by envelope event ID. These events are informational. Only an Access resolve response authorizes a user, and `access.authz.changed.v1` remains the cache invalidation signal.

## Consequences

Notifications can use opaque IDs. Any later need for display data requires an authorized, documented Access contract. The ID, timestamp, and forbidden-field rules are proposed public contract details for owner review. No new endpoint, table, or permission is introduced.
