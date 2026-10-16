# ADR-0065: Bounded tenant bulk role assignment

Status: proposed
Date: 2026-10-16
Owner: agent; owner approved scoped Access worker assignment and role/member grants

## Context

WP-10 requires asynchronous bulk assignment with progress, audit, and per-tenant concurrency. The handbook does not define the public request contract, batch size, or how queued authority is rechecked. Reusing a human token in a worker would violate the machine-identity model, and trusting approval from submission time would permit stale or escalated grants.

## Decision

An active tenant owner with `access.assignment.manage` and recent MFA may queue one batch of 1–100 distinct, active, nonsupport tenant members for one nonprivileged role. The API rejects cross-tenant targets, owner/self targets, expired grants, unsafe roles, and unsafe reason text. It stores target UUIDs only in a class T, forced-RLS table. `POST /v1/tenants/{tenantId}/role-assignment-batches` returns an opaque batch ID and progress; `GET .../{batchId}` returns counts and status. Only one unfinished batch per tenant is allowed.

The existing tenant scheduler processes at most 100 targets per sweep and at most four tenants concurrently. Each target is one transaction. The worker runs as `access_worker` with a configured machine actor, rechecks tenant and requesting owner's live membership, identity, owner role and permission, role safety, target membership and identity, and duplicate grants. It advances the cursor atomically with an audit outcome. Successful grants insert an assignment, bump authorization version, and write existing assignment and invalidation outbox events. Invalidated requests count as failed; a transient database error rolls back the cursor and is retried. The worker never logs target IDs. The application role cannot update job progress; the worker cannot create jobs.

## Consequences

The batch contract intentionally excludes privileged roles and nonowner administrators; those grants stay in the existing per-assignment flow. Progress is aggregate counts, with no target identifiers in status URLs or responses. A per-tenant partial unique index and scheduler lock bound concurrent work. Disposable PostgreSQL tests exercise HTTP tenant gates, wrong-context RLS, idempotent processing, and owner revocation after queueing. INV-02, INV-03, INV-04, INV-07, INV-10, INV-11, INV-12, and INV-16 apply.
