# ADR-0051: Accepted tenant ownership transfer

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-06 requires owner nomination and target acceptance. Ordinary role assignment refuses the system `owner` role. A nomination needs durable state but must grant no authority before acceptance.

## Decision

The current owner nominates an active ordinary member with recent MFA and a machine-safe reason. A class T `ownership_transfers` row records the nomination under forced RLS and expires after seven days. One pending nomination per owner and tenant is permitted. The nominee accepts through a tenant-gated endpoint requiring `access.app.access`; the service verifies the nominee, current owner status, expiry, and membership inside a tenant-locked transaction. Acceptance inserts the nominee's owner assignment and revokes the nominator's owner assignment atomically, then writes audit and the existing assignment and authorization-change events. The nomination grants no role. The nominator's step-up is required; the handbook does not require separate nominee step-up.

## Consequences

The table requires migration 010 before the API uses it. A denied operation is audited with a safe reason code. This public and data-model decision needs owner review. INV-02, INV-03, INV-04, INV-08, INV-10, INV-11, INV-12, and INV-16 apply.
