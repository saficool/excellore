# RFC-0002: Minimize tenant metadata events

Status: accepted by platform owner @saficool on 2026-10-14

## Problem

Handbook Appendix H required tenant names, slugs, plans, app lists, and optional free-form features in tenant lifecycle events. Names and slugs can identify a sole proprietor, and free-form features could contain personal values. INV-11 prohibits personal data in events.

## Decision

The owner chose opaque IDs and bounded machine-safe codes. `tenant.created.v1` carries `tenantId`; `tenant.updated.v1` carries `tenantId` and changed field names only; `tenant.app.enabled.v1` and `tenant.app.disabled.v1` carry `tenantId` and bounded `appId`. Names, slugs, plans, prior slugs, app lists, and feature values are omitted. No invariant changes.

## Consequences

Consumers needing current values call an authorized Access contract. Slug-cache consumers maintain a reverse index from tenant ID to cached slug keys, or clear the slug cache namespace on tenant update. The latter is less precise but fails safely. The handbook and schemas define these payloads in version 4.3.
