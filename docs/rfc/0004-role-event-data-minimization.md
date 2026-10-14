# RFC-0004: Minimize Access role events

Status: accepted by platform owner @saficool on 2026-10-14

## Problem

Handbook Appendix H required a role `name` and optional `permissions` array in Access role events. Tenant-defined role names may include personal values, conflicting with INV-11. Copied permission lists can also become stale and must not become an authorization source (INV-02, INV-08).

## Decision

The owner chose opaque IDs only. `access.role.created.v1`, `updated.v1`, and `deleted.v1` carry `tenantId` and `roleId`. They omit names, descriptions, and permission lists. Consumers needing current role details or permissions use the Access authority. No invariant changes.

## Consequences

The handbook and schemas define the minimized payloads in version 4.5. These events remain informational; `access.authz.changed.v1` handles cache invalidation. Producers must not add custom names or permission values to role event payloads.
