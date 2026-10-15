# ADR-0040: Tenant provisioning and identity mirror boundary

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-05 requires a minimal platform tenant creation flow, an owner invitation placeholder, global identity JIT upsert, and `user_tenant_index` maintenance. The handbook activates a tenant and grants `owner` only when its invitation is accepted. The invitation delivery and acceptance flow belongs to WP-06.

## Decision

`POST /v1/platform/tenants` requires the `Platform.Operator` policy and an `api-access` token. It validates a safe slug, plan, name, app IDs, and owner email; creates a `provisioning` tenant; enables registered core and requested apps; creates tenant settings; and stores a pending owner invitation with the global `owner` role ID. The invitation uses a random 256-bit secret hashed with SHA-256, but discards the secret. It cannot be accepted until the later invitation service rotates and delivers a token. The response contains opaque IDs and status, not the email. Neither owner membership nor `tenant.created.v1` is produced yet.

Shell is the portal and does not have an app catalog entry or `tenant_apps` row. Profile and Access are the registered core apps. The tenant's `tenant_apps`, settings, invitation, and audit rows are written under a transaction-local tenant context. The tenant-scoped audit contains the tenant ID, operator ID, action, and a fixed reason, without the name, slug, or email.

The global identity mirror upserts only from a validated human `api-access` token on user or platform API paths. It never changes tenant membership or identity status, and it emits opaque global identity events only for a new identity or changed named fields. Missing or unverified email cannot be used to accept an invitation. A database trigger maintains `user_tenant_index` in the same transaction as every membership insert, update, or delete; the index remains a reviewed class P RLS exemption.

## Consequences

INV-02, INV-03, INV-06, INV-08, INV-10, INV-11, INV-12, INV-13, INV-14, and INV-16 apply. The placeholder invitation is intentionally undeliverable, and the tenant remains unavailable until WP-06 builds owner acceptance. Tests must cover the provisioning state, core app gates, foreign tenant isolation, index rollback, and identity event idempotency. This proposed security and contract choice needs owner review.
