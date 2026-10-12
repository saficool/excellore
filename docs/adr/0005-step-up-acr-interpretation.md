# ADR-0005: Interpret ACR level 2 as completed step-up

Status: proposed
Date: 2026-10-12
Owner: agent: needs human review

## Context

The handbook sections 10.7, 11.6, and 20.4 require privileged actions to check the access token's higher `acr` and expose `ICurrentUser.HasRecentMfa`. It does not define an additional elapsed-time limit for MFA. The realm currently emits `acr=2` after the step-up flow. INV-15 requires using standard OIDC claims; INV-10 requires failed checks to deny access.

## Decision

For this first shared authentication slice, `HasRecentMfa` is true only when the validated access token has `acr=2`. The API uses the configured five-minute token lifetime, and the shared library does not infer MFA from a client-provided flag or an unvalidated token. A future requirement for a shorter MFA freshness window must define the interval and use a validated `auth_time` claim or a new Keycloak step-up policy.

## Consequences

Privileged endpoints can consistently require the higher Keycloak authentication level. The name `HasRecentMfa` currently means that the token attests to level 2; it does not enforce a separate time window. Review this interpretation before privileged endpoints ship in WP-06.
