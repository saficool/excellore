# ADR-0034: Access internal lookups and BFF tenant gate

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-04 requires a BFF client for Access's internal authorization contracts and a shared requireTenant helper. The handbook's tenant-list and slug-lookup contracts previously placed a user's opaque identifier in a URL, conflicting with INV-11. The owner approved moving both lookups to POST bodies.

## Decision

The Access service will implement POST /internal/v1/users/tenants:list with a userId body and POST /internal/v1/tenants:resolve-slug with userId and slug in the body. Both require a machine token with the access.authz.resolve role. The BFF uses its app-specific svc-*-authz client credentials to call those routes and POST /internal/v1/authz/resolve. It caches its machine token until shortly before expiry. It does not pass a browser token to Access.

requireTenant obtains the user from the server-side session, resolves the slug through Access, then checks a fresh authorization snapshot before returning tenant context. A non-member or denied snapshot becomes 404. A member without the app's enabled app entry and app.access permission becomes 403. Mismatched or malformed authority responses and network failures become 503. This first implementation requests fresh context on every call; later BFF caching may use the handbook's 60-second maximum.

## Consequences

INV-02, INV-05, INV-10, and INV-11 are preserved. The Access service in WP-05 must implement the two revised POST contracts. The app template in WP-04 will map the shared helper's typed errors to its login redirect, not-found page, and access-denied response. Security review is required before accepting this ADR.
