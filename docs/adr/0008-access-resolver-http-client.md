# ADR-0008: Access resolver HTTP client validation

Status: proposed
Date: 2026-10-12
Owner: agent: needs human review

## Context

The handbook sections 7.8 and 20.8 specify a private Access resolve contract and cached machine token, but leave response validation and HTTP transport details to the client. A permissive parser could cache a decision for the wrong tenant or turn an ambiguous response into an allow. INV-02, INV-06, INV-09, INV-10, INV-11, and INV-15 apply.

## Decision

The shared client sends user and tenant IDs in the JSON body only, uses OAuth 2.0 client credentials with HTTP Basic client authentication, and caches the bearer token until 60 seconds before expiry. It accepts only HTTP 200. Every response must contain the requested `tenantId` and a string permission array. An explicit `deny` must have no permissions. An allow (including the handbook's allow example without `decision`) requires active tenant and membership statuses. Unknown decisions, missing fields, wrong tenant IDs, transport failures, and oversized replies fail closed as `AuthzUnavailableException` with a fixed message. The client does not log response bodies, credentials, or tokens.

The token endpoint requires HTTPS, except when a caller explicitly opts into HTTP for local development. The Access service origin may use HTTP on the private network, as Appendix D shows. Both HTTP clients reject redirects, preventing credentials or tenant IDs from being replayed to a redirect target. The Access base URL must be an origin so relative path resolution cannot alter the internal route.

## Consequences

Access must return `tenantId` and `permissions: []` on denials. Operational failures return `503` through the tenant gate. Integration with a live Access service remains for WP-05; this slice verifies the contract with HTTP handlers. Review the response shape and local HTTP exception before exposing tenant endpoints.
