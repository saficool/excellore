# ADR-0060: Dedicated Access BFF authority client

Status: proposed
Date: 2026-10-16
Owner: agent; needs human security review

## Context

The WP-08 Access BFF uses the shared `requireTenant` helper, which resolves a slug and permissions through Access internal contracts. Those contracts require a machine identity with `access.authz.resolve`. The Keycloak realm already gives one such client to Shell and Profile, but none to the Access BFF. Reusing either app's secret would weaken service identity separation. INV-02, INV-05, INV-06, INV-08, and INV-11 apply.

## Decision

Declare `svc-access-authz` as a confidential service-account client with only the `api-access` audience and `access.authz.resolve` role. Give it a separate secret, managed by the existing local secret generation and deployment secret process. The Access BFF uses it only for Access internal tenant resolution. Its browser session still uses `excellore-access` and does not receive the machine credential. The local reconciliation script creates and scopes the client on existing development realms; production realm state remains code-managed.

## Consequences

The Access BFF can use the shared authorization boundary without sharing another app's machine identity. Realm verification checks the new client and its limited token claims. This changes Keycloak configuration and requires human security review before production use.
