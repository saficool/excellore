# ADR-0032: Erasure confirmation event contracts

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-03 requires the `<service>.erasure.confirmed.v1` catalog pattern. The current backend services that erase data are Access and Profile. Tenant erasure requires tenant context (INV-07); a user erasure request spans tenants and has no single tenant ID. The handbook gives `scope`, optional `tenantId` and `userId`, and `service`, but does not bind those fields to the envelope or define concrete service types.

## Decision

Define `access.erasure.confirmed.v1` and `profile.erasure.confirmed.v1` with separate schemas and typed C# records. The event `source` and data `service` must agree with the type. Exactly one scope applies:

- `scope: "tenant"`: require a nonempty data `tenantId` equal to envelope `tenantid`; omit `userId`.
- `scope: "user"`: require a nonempty data `userId`; omit both data `tenantId` and envelope `tenantid`.

Explicit null in the unused data ID is rejected; unrelated unknown fields remain allowed for forward compatibility. Known personal values, object keys, links, tokens, and free-form details are rejected. Consumers parse before work and de-duplicate by envelope event ID. Tenant confirmations run in tenant context. A global user confirmation does not authorize tenant work; any tenant-specific cleanup must be performed separately under each tenant's context.

## Consequences

Access can distinguish confirmation by service and scope without trusting an arbitrary service label or mixing tenant and user erasure. Each concrete type has a tenant and a user example. Adding another data-owning backend service requires a new concrete type and schema. These public contract details are proposed for owner review. No erasure handler, deletion flow, endpoint, table, or permission is introduced here; those remain in WP-12.
