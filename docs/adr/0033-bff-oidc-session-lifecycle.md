# ADR-0033: BFF OIDC session lifecycle

Status: proposed
Date: 2026-10-14
Owner: agent: needs human review

## Context

WP-04 requires a shared OIDC client, Valkey-backed server sessions, refresh locking, and back-channel logout. The handbook requires encrypted token storage and an opaque browser cookie. Section 12.1 previously placed `id_token_hint` in a logout URL, conflicting with INV-11. The owner approved a server-side POST correction.

## Decision

`@excellore/auth` uses `openid-client` for discovery, authorization code with PKCE S256, state, nonce, code exchange, ID token signature verification against the issuer JWKS, and refresh. A one-use, five-minute Valkey login transaction is bound to the browser by an HttpOnly flow cookie. An app session has a fresh 256-bit opaque cookie ID, 30-minute sliding idle expiry, and nine-hour absolute expiry. Access, refresh, and ID tokens are stored only in a Valkey session encrypted as standard AES-256-GCM JWE using `jose`; neither tokens nor tenant context enter browser cookies. HMAC-derived secondary indexes allow Keycloak `sid` or subject logout without raw subject identifiers in keys. A Valkey lease serializes refresh across BFF instances. Compare-and-set prevents refresh or touch from recreating a deleted session.

For RP-initiated logout, the BFF verifies the browser Origin, sends `id_token_hint` in a server-side form POST to Keycloak's discovered end-session endpoint, deletes the local session, and redirects the browser to the app root without a token in its URL. The BFF uses no `post_logout_redirect_uri` because this is a server-side call. A failed remote logout returns a generic error after local session deletion. Keycloak's back-channel logout token is verified for signature, issuer, audience, time, required event, and absence of nonce before session deletion. Login `returnTo` values are limited to relative paths without query strings. Local development may use HTTP and a non-`__Host-` cookie only with an explicit flag and a `.localhost` host under development or test mode. Production requires HTTPS and a `__Host-` cookie.

## Consequences

INV-05, INV-11, and INV-15 remain intact without placing an ID token in a URL. The local dev-realm test verified that Keycloak accepts the sessionless RP-initiated POST and rejects refresh after logout. The first WP-04 group provides Web Request/Response handler factories; Next.js route wiring and a generated app are later groups. Dependency licenses checked at adoption: `openid-client` 6.8.8, `iovalkey` 0.4.0, `jose` 6.2.12, `zod` 4.6.7, and `@types/node` 22.19.15 are MIT.
