# ADR-0043: Access tenant read gate and catalog-backed step-up

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-06 adds tenant endpoints to the Access authority. Access already has the authoritative resolver and shared `Excellore.Authz` gate, but its API did not use the gate. A tenant read must use the validated token subject, verify active membership and its declared permission before the handler, and constrain subsequent database reads with transaction-local RLS context (INV-02, INV-03, INV-04, INV-10, INV-16). Step-up decisions must reflect the deployed code-first permission catalog rather than a second hard-coded list.

## Decision

Access resolves its own tenant requests through `AccessResolutionService` using its local database. It does not call its internal HTTP contract or accept a browser-supplied user ID. The shared tenant gate runs on every `/v1/tenants/{tenantId}` endpoint and checks the required permission before handlers. Access reads privilege flags from the synchronized `permissions` table for each gated request. An unknown permission requires MFA; database unavailability fails closed. Shared authz supports asynchronous privilege classification while retaining a conservative default for existing apps.

Tenant role and permission readers take the tenant only from the scoped gate context and set `app.tenant_id` for their database transaction. The role reader exposes system roles and roles of that tenant. The permission picker shows only non-deprecated permissions of enabled apps. The initial read endpoints are `me/access`, `roles`, `roles/{roleId}`, and `permissions`, as indexed in Appendix I.

## Consequences

The extra catalog lookup adds one database query per gated permission. This keeps step-up decisions current after catalog sync and avoids a second mutable privilege list. HTTP integration tests exercise all four routes across two tenants, a member without the read permission, and a foreign role ID. The same gate will protect later WP-06 mutations. This proposed security decision needs owner review. INV-02, INV-03, INV-04, INV-06, INV-10, INV-11, INV-16, and INV-17 apply.
