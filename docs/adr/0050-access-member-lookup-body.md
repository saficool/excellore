# ADR-0050: Access member lookup in a request body

Status: proposed
Date: 2026-10-15
Owner: agent: needs human review

## Context

Appendix I specified `GET /members/{userId}`. A user identifier in a URL conflicts with INV-11. The owner chose a body-based lookup, consistent with the other Access member routes.

## Decision

Use `POST /v1/tenants/{tenantId}/members:lookup` with an opaque `userId` in the JSON body. The tenant gate and `access.member.read` run before the handler. The query joins `identities` only through `tenant_members` under forced RLS; it returns 404 for a missing, removed, support, or foreign member. The member list uses the same scope. These read operations write no audit event and expose personal details only in the authorized API response.

## Consequences

Clients must use the new body-based contract. The list and lookup routes are covered by cross-tenant tests. INV-03, INV-04, INV-10, INV-11, and INV-16 apply.
