# ADR-0041: Access internal resolver boundary

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

WP-05 requires an authoritative permission resolver and private tenant lookups. The `user_tenant_index` is class P and RLS-exempt, while membership, role, and app data are class T or H. The shared clients already use POST bodies for user IDs under the owner's prior decision (ADR-0034).

## Decision

The three internal routes require a validated `api-access` machine token with `access.authz.resolve`, from a `svc-*-authz` client. The catalog route requires `svc-catalog-sync` plus `access.catalog.sync`. User tokens cannot use either machine policy, even if they contain a client role. All three routes return generic validation errors without user identifiers.

The resolver first checks the platform tenant state, then opens a transaction-local tenant context and checks the actual class T membership and global identity state. It unions current direct and group grants, excludes expired or revoked grants, intersects permissions with enabled apps, and includes Shell for every active member. The core Profile and Access apps remain enabled by their class P `core` flag. A deprecated catalog row still grants an existing assignment until its role grant is removed; deprecation itself is not deletion. Denials return the same 200 response with the requested tenant ID, `decision: deny`, and empty permissions, without names or status details.

The list route uses `user_tenant_index` only to find candidates and rechecks each candidate's class T membership through the resolver before returning tenant metadata. Slug lookup similarly checks class T membership before returning an ID. No lookup trusts a body tenant ID or the RLS-exempt index as authority.

## Consequences

INV-01, INV-02, INV-03, INV-06, INV-08, INV-10, INV-11, INV-13, INV-16, and INV-17 apply. Tests cover machine policy denials, expired grants, app gating, inactive tenants and members, cross-tenant lookups, and uniform denial. This proposed security decision needs owner review.
