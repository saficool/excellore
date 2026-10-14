# ADR-0036: Generated Next.js BFF baseline

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-04 requires a generated app that can log in against the development realm. The Access authority and app APIs arrive in later work packages. A template must still fail closed for tenant access, keep browser tokens out of reach, and make security controls consistent across future apps.

## Decision

`create-excellore-app` generates a Next.js App Router application that delegates OIDC and session handling to `@excellore/auth`. It exposes the standard auth routes and a server-rendered account page. The generated Profile app is the first runnable instance. Local credentials are written only to ignored `.env.local` by `scripts/setup-next-local.mjs`; the session encryption key is generated once per app and retained across restarts.

The shared auth package owns the API proxy policy: authenticated requests, Access-resolved tenant context, token refresh, a same-origin CSRF header for writes, no query strings, no redirects, JSON-only responses, and `no-store`. The generated app supplies only its app ID and deployment configuration. Tenant pages use the same Access gate and shared header. Until WP-05 supplies Access, these pages cannot display tenant data. A nonce-based CSP and standard security headers are set by the Next.js proxy on every app response.

Step-up requests ask Keycloak for ACR 2 and verify the returned ACR and subject. Account actions use an explicit `kc_action` allow-list and bind the returned subject to the existing session. All auth flows keep tokens in Valkey, not in browser responses.

## Consequences

INV-02, INV-05, INV-10, INV-11, and INV-16 are covered by shared tests and a live generated-app login check. Browser code must send `X-Excellore-CSRF: 1` on mutating API calls and must send data in request bodies. App API base URLs and Access endpoints must be configured at deployment. The local Profile app demonstrates login before its WP-09 features exist. This proposed security decision needs owner review. Next.js 16.4.0, React 19.3.0, Tailwind 4.3.3, and server-only 0.0.1 were checked as MIT licensed at adoption.
