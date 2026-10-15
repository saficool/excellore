# ADR-0052: Tenant metadata settings

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-06 and Appendix I require tenant settings routes. The initial console names tenant name, slug, and security options. Q-18 defers tenant-defined security policies, so the first write contract is limited to name and slug. Slugs can identify a person and must stay out of events and audit records under INV-11.

## Decision

`GET /settings` returns the tenant's name, slug, and plan to an authorized member. `PATCH /settings` accepts optional name or slug plus a required machine-safe reason code. The route and service both verify `access.settings.manage`; the service also requires recent MFA and an active tenant. The service rejects reserved or malformed slugs and catches the database uniqueness constraint. It audits changed field names without values and publishes the existing `tenant.updated.v1` event with changed field names only. Tenant-defined security controls and changes to plan or app enablement are excluded.

## Consequences

Clients must refresh slug-based links after a successful change. Event consumers invalidate slug caches on `tenant.updated.v1`. This security and public-contract decision needs owner review. INV-03, INV-04, INV-10, INV-11, INV-12, and INV-16 apply.
