# ADR-0061: Keycloak subject scope on API access tokens

Status: proposed
Date: 2026-10-16
Owner: agent; needs human security review

## Context

The local Keycloak 26 realm explicitly lists client default scopes but omits `basic`. Keycloak maps `sub` into access tokens through that scope. The Access API requires a UUID subject and denied real local user tokens before any handler ran, despite valid audience, email, and platform role. BFF tenant resolution also needs `sub`. INV-01, INV-02, INV-06, and INV-13 apply.

## Decision

Attach Keycloak's standard `basic` client scope to every Excellore BFF and machine client in the realm as code. Reconcile it for existing local development clients without resetting the realm. Verify the standard UUID `sub` in issued user and machine access tokens. Keep tenant, role, and permission data out of user token claims as before.

## Consequences

The Access API can identify the global user and enforce tenant authorization. The scope also adds Keycloak's standard authentication-time mapper; it does not grant access. This realm change needs human security review before production use. Keycloak documents `basic` as the default source of the access-token `sub` mapper.
