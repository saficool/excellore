# RFC-0003: Minimize member-added events

Status: accepted by platform owner @saficool on 2026-10-14

## Problem

Handbook Appendix H required a `roles` array in `tenant.member.added.v1`. Tenants can define custom role names, which may include personal values. INV-11 prohibits personal data in events. Role assignments are also owned by the Access authority (INV-08), so a copied role list could become stale.

## Decision

The owner chose opaque identifiers. `tenant.member.added.v1` carries `tenantId`, `userId`, and `membershipId` only. It omits role names and permission values. Other membership status events carry only `tenantId` and `userId`. No invariant changes.

## Consequences

Consumers needing current assignments ask Access through its authorized internal contract. The handbook example and Appendix H now match the IDs-only contract in version 4.4.
