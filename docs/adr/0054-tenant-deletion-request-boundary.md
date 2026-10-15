# ADR-0054: Tenant deletion request boundary

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-06 requires the tenant deletion-request endpoint. WP-12 owns the later erasure and confirmation workflow. The handbook's binding grace period is 14 days.

## Decision

An active owner with `access.tenant.delete` and recent MFA submits a machine-safe reason. Access atomically marks the tenant `pending_deletion`, records the request and due time, audits the transition, bumps its authorization version, and enqueues the existing `tenant.deletion.requested.v1` event. Resolution denies all memberships once the transaction commits. The WP-12 worker will execute the later deletion, erasure, and confirmation steps after the grace period.

## Consequences

This route begins a major lifecycle change and has no self-service cancellation contract yet. The later WP-12 flow must define restoration during the grace period. This security and public-contract decision needs owner review. INV-02, INV-04, INV-08, INV-10, INV-11, INV-12, and INV-16 apply.
